// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Microsoft.Data.Sqlite;
using Omabot.Common;
using Omabot.Config;
using Omabot.Containers;
using Omabot.Db;
using Omabot.Egress;
using Omabot.Supervision;
using Omabot.Team;

namespace Omabot.Desktops;

public sealed record DesktopRow(string Name, string Mode, string? Owner, int Port, string State, DateTimeOffset? LastUsedAt)
{
    public string Container => ServiceContainerSpec.DesktopPrefix + Name;
}

public sealed record DesktopLease(long Id, string Desktop, string Bot, string? AssignmentId, DateTimeOffset Since, DateTimeOffset ExpiresAt);

/// <summary>
/// Desktops and leases (plan 7.7). Modes: own (one per bot), shared (one
/// desktop, one bot at a time), pool (up to N desktops for whoever needs
/// one). At most desktops.max in all. A lease belongs to one bot and one
/// piece of work, ends with that work or after desktops.lease_minutes
/// without use, and a desktop with no lease and no use for
/// desktops.idle_minutes is stopped. A home folder survives a stop.
/// </summary>
public sealed class DesktopManager(RosterDb db, RosterConfig config, RosterPaths paths, IClock clock, AuditLog audit)
{
    public const string Shared = "shared";
    public static string Pool(int i) => "pool-" + i.ToString(CultureInfo.InvariantCulture);

    private static DateTimeOffset? T(SqliteDataReader r, string c)
        => RosterDb.Str(r, c) is { } s ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : null;

    private static DesktopRow Map(SqliteDataReader r) => new(r.GetString(0), r.GetString(1), RosterDb.Str(r, "owner"), r.GetInt32(3), r.GetString(4), T(r, "last_used_at"));

    private static DesktopLease MapLease(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), RosterDb.Str(r, "assignment_id"), T(r, "since")!.Value, T(r, "expires_at")!.Value);

    private const string Cols = "name, mode, owner, port, state, last_used_at";
    private const string LeaseCols = "id, desktop, bot, assignment_id, since, expires_at";

    /// <summary>The desktops the team needs. Throws when it needs more than the cap.</summary>
    public static IReadOnlyList<(string Name, string Mode, string? Owner)> Desired(IEnumerable<BotSpec> bots, RosterConfig config)
    {
        var list = new List<(string, string, string?)>();
        var specs = bots.ToList();
        foreach (var b in specs.Where(b => b.Desktop == "own").OrderBy(b => b.Slug, StringComparer.Ordinal)) list.Add((b.Slug, "own", b.Slug));
        if (specs.Any(b => b.Desktop == "shared")) list.Add((Shared, "shared", null));
        if (specs.Any(b => b.Desktop == "pool"))
            for (var i = 1; i <= config.Desktops.PoolSize; i++) list.Add((Pool(i), "pool", null));
        if (list.Count > config.Desktops.Max)
            throw new InvalidDataException($"The team needs {list.Count} desktops; the cap is {config.Desktops.Max} (desktops.max).");
        return list;
    }

    /// <summary>Adds missing desktops with a fixed port each. Desktops no bot needs any more are stopped, not deleted.</summary>
    public IReadOnlyList<string> Sync(IEnumerable<BotRow> bots)
    {
        var lines = new List<string>();
        var want = Desired(bots.Where(b => b.Enabled).Select(b => b.Spec), config);
        var have = All().ToDictionary(d => d.Name, StringComparer.Ordinal);
        foreach (var (name, mode, owner) in want.Where(w => !have.ContainsKey(w.Name)))
        {
            var used = All().Select(d => d.Port).ToHashSet();
            var port = Enumerable.Range(config.Desktops.PortBase, config.Desktops.Max).FirstOrDefault(p => !used.Contains(p));
            if (port == 0) throw new InvalidDataException("No free desktop port.");
            db.Exec($"INSERT INTO desktops({Cols}, created_at, updated_at) VALUES ($n,$m,$o,$p,'stopped',NULL,$t,$t);",
                ("$n", name), ("$m", mode), ("$o", owner), ("$p", port), ("$t", RosterDb.Now(clock)));
            audit.Write("roster", "desktop.create", name, $"{mode} on 127.0.0.1:{port}");
            lines.Add($"desktop {name} ({mode}) on 127.0.0.1:{port}");
        }
        return lines;
    }

    public IReadOnlyList<DesktopRow> All() => db.Query($"SELECT {Cols} FROM desktops ORDER BY name;", Map);

    public DesktopRow? Get(string name) => db.Query($"SELECT {Cols} FROM desktops WHERE name=$n;", Map, ("$n", name)).FirstOrDefault();

    public IReadOnlyList<DesktopLease> ActiveLeases() => db.Query($"SELECT {LeaseCols} FROM desktop_leases WHERE released_at IS NULL ORDER BY id;", MapLease);

    public DesktopLease? LeaseOf(string desktop) => ActiveLeases().FirstOrDefault(l => l.Desktop == desktop);

    /// <summary>Whose allowlist the desktop uses at the gate right now: the owner, or the bot holding its lease.</summary>
    public string? AllowFrom(DesktopRow d) => d.Mode == "own" ? d.Owner : LeaseOf(d.Name)?.Bot;

    /// <summary>
    /// A desktop for a bot's work: its own, the shared one when free, or a
    /// free pool desktop. An existing lease by the same bot is reused.
    /// </summary>
    public (DesktopRow? Desktop, string? Problem) Acquire(BotRow bot, string? assignmentId)
    {
        var mode = bot.Spec.Desktop;
        if (mode == "off") return (null, $"{bot.Name} has no desktop (desktop: off).");
        var candidates = mode switch
        {
            "own" => All().Where(d => d.Name == bot.Slug).ToList(),
            "shared" => All().Where(d => d.Name == Shared).ToList(),
            _ => All().Where(d => d.Mode == "pool").ToList(),
        };
        if (candidates.Count == 0) return (null, $"No {mode} desktop is set up yet; the service creates it on its next tick.");
        var leases = ActiveLeases();
        var mine = leases.FirstOrDefault(l => l.Bot == bot.Slug && candidates.Any(c => c.Name == l.Desktop));
        if (mine is not null)
        {
            Touch(mine.Desktop);
            return (Get(mine.Desktop), null);
        }
        var free = candidates.FirstOrDefault(c => leases.All(l => l.Desktop != c.Name));
        if (free is null)
        {
            var holder = leases.First(l => candidates.Any(c => c.Name == l.Desktop));
            return (null, mode == "shared" ? $"The shared desktop is in use by {holder.Bot}; try again later." : "Every pool desktop is in use; try again later.");
        }
        var now = clock.UtcNow;
        try
        {
            db.Exec("INSERT INTO desktop_leases(desktop, bot, assignment_id, since, expires_at) VALUES ($d,$b,$a,$s,$e);",
                ("$d", free.Name), ("$b", bot.Slug), ("$a", assignmentId), ("$s", RosterDb.Now(clock)), ("$e", Iso(now + TimeSpan.FromMinutes(config.Desktops.LeaseMinutes))));
        }
        catch (SqliteException)
        {
            return (null, "Another bot took that desktop at the same moment; try again.");
        }
        audit.Write("bot:" + bot.Slug, "desktop.lease", free.Name, assignmentId ?? "");
        Touch(free.Name);
        return (Get(free.Name), null);
    }

    /// <summary>A tool call on the desktop: keeps it awake and extends its lease.</summary>
    public void Touch(string name)
    {
        var now = clock.UtcNow;
        db.Exec("UPDATE desktops SET last_used_at=$t, updated_at=$t WHERE name=$n;", ("$t", Iso(now)), ("$n", name));
        db.Exec("UPDATE desktop_leases SET expires_at=$e WHERE desktop=$n AND released_at IS NULL;", ("$e", Iso(now + TimeSpan.FromMinutes(config.Desktops.LeaseMinutes))), ("$n", name));
    }

    public int Release(string botSlug, string? assignmentId, string reason)
        => db.Exec("UPDATE desktop_leases SET released_at=$t, reason=$r WHERE bot=$b AND ($a IS NULL OR assignment_id=$a) AND released_at IS NULL;",
            ("$t", RosterDb.Now(clock)), ("$r", reason), ("$b", botSlug), ("$a", assignmentId));

    /// <summary>Ends leases whose work ended or that went unused past their time.</summary>
    public IReadOnlyList<string> ExpireLeases(Func<string, bool> workLive)
    {
        var lines = new List<string>();
        foreach (var l in ActiveLeases())
        {
            var reason = l.ExpiresAt <= clock.UtcNow ? "unused past its lease time" : l.AssignmentId is { } a && !workLive(a) ? "its work ended" : null;
            if (reason is null) continue;
            db.Exec("UPDATE desktop_leases SET released_at=$t, reason=$r WHERE id=$i AND released_at IS NULL;", ("$t", RosterDb.Now(clock)), ("$r", reason), ("$i", l.Id));
            lines.Add($"desktop {l.Desktop}: lease of {l.Bot} ended ({reason})");
        }
        return lines;
    }

    /// <summary>Running desktops with no lease and no use for desktops.idle_minutes.</summary>
    public IReadOnlyList<DesktopRow> Idle()
    {
        var leased = ActiveLeases().Select(l => l.Desktop).ToHashSet(StringComparer.Ordinal);
        var cutoff = clock.UtcNow - TimeSpan.FromMinutes(config.Desktops.IdleMinutes);
        return [.. All().Where(d => d.State == "running" && !leased.Contains(d.Name) && (d.LastUsedAt is null || d.LastUsedAt <= cutoff))];
    }

    public void SetState(string name, string state) => db.Exec("UPDATE desktops SET state=$s, updated_at=$t WHERE name=$n;", ("$s", state), ("$t", RosterDb.Now(clock)), ("$n", name));

    /// <summary>The desktop's home on the host: bots/BOT/desktop for an own desktop, desktops/NAME/home otherwise.</summary>
    public string HomeOf(DesktopRow d) => d.Mode == "own" && d.Owner is { } o ? Path.Combine(paths.Bot(o).Root, "desktop") : Path.Combine(paths.Home, "desktops", d.Name, "home");

    public string Url(DesktopRow d) => $"http://{d.Container}:{config.Desktops.McpPort.ToString(CultureInfo.InvariantCulture)}/mcp";

    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}

/// <summary>View-only noVNC passwords, one per desktop, 0600 under secrets/desktops.</summary>
public sealed class DesktopSecrets(RosterPaths paths, Redactor redactor)
{
    public string Dir => Path.Combine(paths.Secrets, "desktops");

    public string FileFor(string desktop) => Path.Combine(Dir, desktop + ".vnc");

    public string Ensure(string desktop)
    {
        Names.RequireSafeSlug(desktop, "desktop");
        SecureFile.EnsureDirNoClobber(paths.Secrets);
        SecureFile.EnsureDirNoClobber(Dir);
        var file = FileFor(desktop);
        if (!File.Exists(file))
        {
            const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
            var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
            SecureFile.WriteIfMissing(file, new string([.. bytes.Select(b => alphabet[b % alphabet.Length])]) + "\n");
        }
        var pw = File.ReadAllText(file).Trim();
        redactor.AddKnownSecret(pw);
        return pw;
    }
}

/// <summary>Starts, stops and hands out desktops through the supervisor.</summary>
public sealed class DesktopService(DesktopManager desktops, ISupervisorClient supervisor, EgressClients egress, DesktopSecrets secrets, RosterConfig config, RosterPaths paths, AuditLog audit)
{
    public DesktopManager Desktops => desktops;

    public ServiceContainerSpec SpecFor(DesktopRow d)
    {
        secrets.Ensure(d.Name);
        var home = desktops.HomeOf(d);
        SecureFile.EnsureDirNoClobber(Path.GetDirectoryName(home)!);
        Directory.CreateDirectory(home);
        var spec = new ServiceContainerSpec(
            d.Container, "desktop", config.Desktops.Image, config.Desktops.User, [],
            new Dictionary<string, string>
            {
                ["HTTPS_PROXY"] = egress.ProxyUrl(EgressClients.Desktop(d.Name)),
                ["HTTP_PROXY"] = egress.ProxyUrl(EgressClients.Desktop(d.Name)),
                ["NO_PROXY"] = "",
                ["VNC_PASSWORD_FILE"] = "/run/secrets/vnc-password",
                ["VNC_VIEW_ONLY"] = "1",
                ["MCP_PORT"] = config.Desktops.McpPort.ToString(CultureInfo.InvariantCulture),
            },
            [new Mount(home, "/home/desktop", false), new Mount(secrets.FileFor(d.Name), "/run/secrets/vnc-password", true)],
            ["/tmp"],
            new Dictionary<string, string> { ["oma-bot.roster"] = "1", ["oma-bot.role"] = "desktop", ["oma-bot.desktop"] = d.Name },
            [new NetworkAttach(Networks.Desktops(config).Name, [d.Container])],
            [new PortMap(d.Port, config.Desktops.NoVncPort)],
            ContainerSpec.ParseMemory(config.Desktops.Memory), (long)Math.Round(config.Desktops.Cpu * 1_000_000_000d), config.Desktops.Pids, false, 1L << 30);
        return spec with { Labels = new Dictionary<string, string>(spec.Labels) { ["oma-bot.spec"] = spec.Hash() } };
    }

    /// <summary>The desktop MCP address for a bot's work, starting the desktop when needed.</summary>
    public async Task<(string? Url, string? Problem)> EndpointAsync(BotRow bot, string? assignmentId, CancellationToken ct)
    {
        var (d, problem) = desktops.Acquire(bot, assignmentId);
        if (d is null) return (null, problem);
        if (d.State != "running")
        {
            try
            {
                await supervisor.EnsureServiceAsync(SpecFor(d), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ContainerEngineException or HttpRequestException or InvalidOperationException)
            {
                desktops.Release(bot.Slug, assignmentId, "the desktop did not start");
                return (null, "The desktop did not start: " + Redactor.Shared.Redact(ex.Message));
            }
            desktops.SetState(d.Name, "running");
            audit.Write("roster", "desktop.start", d.Name, $"for {bot.Slug}");
        }
        return (desktops.Url(d), null);
    }

    /// <summary>Ends finished leases and stops idle desktops.</summary>
    public async Task<IReadOnlyList<string>> TickAsync(Func<string, bool> workLive, CancellationToken ct)
    {
        var lines = desktops.ExpireLeases(workLive).ToList();
        foreach (var d in desktops.Idle())
        {
            try
            {
                await supervisor.StopServiceAsync(d.Container, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ContainerEngineException or HttpRequestException)
            {
                lines.Add($"desktop {d.Name}: stop failed: {Redactor.Shared.Redact(ex.Message)}");
                continue;
            }
            desktops.SetState(d.Name, "stopped");
            audit.Write("roster", "desktop.stop", d.Name, $"idle for {config.Desktops.IdleMinutes} minutes");
            lines.Add($"desktop {d.Name}: stopped after {config.Desktops.IdleMinutes} idle minutes");
        }
        return lines;
    }

    public string NoVncUrl(DesktopRow d) => $"http://127.0.0.1:{d.Port.ToString(CultureInfo.InvariantCulture)}/vnc.html?view_only=1";

    public RosterPaths Paths => paths;
}
