// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Omabot.Common;
using Omabot.Config;

namespace Omabot.Web;

/// <summary>One web-multi user's roster: its name, its slot number and its own home under users/.</summary>
public sealed record TenantHome(string Name, int Index, RosterPaths Paths)
{
    public string Id => Tenants.Id(Index);
}

/// <summary>
/// web-multi user rosters (plan 7.19, 7.20 item 5). Each user gets a whole
/// roster of their own under users/NAME: roster.db, bots, data folders,
/// secrets, logs and audit, run by its own supervisor and service. The
/// derived roster.json gives it its own container name prefix, Docker
/// networks, subnets, bridges and firewall chains, its own port block and
/// gateway, and its own workspace root, so nothing two users run can meet.
/// </summary>
public static class Tenants
{
    public const int MaxUsers = 60;

    public static string Id(int index) => "u" + index.ToString("D2", CultureInfo.InvariantCulture);

    /// <summary>First port of a user's block: API, gateway, desktops from +20, bots from +100.</summary>
    public static int PortBlock(int index) => 20000 + index * 200;

    public static RosterConfig ConfigFor(RosterConfig host, RosterPaths hostPaths, string name, int index)
    {
        if (index is < 1 or > MaxUsers) throw new ArgumentOutOfRangeException(nameof(index));
        var t = Id(index);
        var c = host.Clone();
        c.Mode = "local";
        c.Web = new WebSettings { SourceUrl = host.Web.SourceUrl };
        c.Fleet = new FleetSettings();
        c.Placement = new PlacementSettings();
        c.HostExec.OpenFirewall = false;
        c.Bots.Tenant = t;
        var b = PortBlock(index);
        c.Api.BindAddress = "127.0.0.1";
        c.Api.Port = b;
        c.Gateway.Port = b + 1;
        c.Desktops.PortBase = b + 20;
        c.Desktops.Max = Math.Min(c.Desktops.Max, 20);
        c.Bots.PortBase = b + 100;
        c.Bots.PortCount = 100;
        var third = index * 4;
        c.Bots.Network = $"oma-{t}-roster";
        c.Bots.Subnet = $"172.31.{third}.0/24";
        c.Bots.Bridge = $"br-oma{t}b";
        c.Gateway.BindAddress = $"172.31.{third}.1";
        c.Desktops.Network = $"oma-{t}-desk";
        c.Desktops.Subnet = $"172.31.{third + 1}.0/24";
        c.Desktops.Bridge = $"br-oma{t}d";
        c.Egress.Network = $"oma-{t}-egress";
        c.Egress.Subnet = $"172.31.{third + 2}.0/24";
        c.Egress.Bridge = $"br-oma{t}e";
        c.Gateway.UpstreamKeyFile = Path.IsPathRooted(host.Gateway.UpstreamKeyFile) ? host.Gateway.UpstreamKeyFile : Path.Combine(hostPaths.Home, host.Gateway.UpstreamKeyFile);
        c.WorkspaceRoots = [Path.Combine(hostPaths.ForUser(name).Home, "work")];
        c.Validate();
        return c;
    }

    public static IReadOnlyList<TenantHome> All(UsersDb db, RosterPaths hostPaths)
        => db.Query("SELECT name, idx FROM homes ORDER BY idx;", r => new TenantHome(r.GetString(0), r.GetInt32(1), hostPaths.ForUser(r.GetString(0))));

    public static TenantHome? Find(UsersDb db, RosterPaths hostPaths, string name)
        => All(db, hostPaths).FirstOrDefault(h => h.Name == name);

    /// <summary>The user's home, made on first use with its own roster.json. An existing roster.json is never overwritten.</summary>
    public static TenantHome Ensure(UsersDb db, RosterConfig host, RosterPaths hostPaths, string name, IClock clock)
    {
        if (!Names.IsSafeSlug(name)) throw new InvalidDataException("A web-multi user roster is named with lower case letters, digits and dashes.");
        var home = Find(db, hostPaths, name);
        if (home is null)
        {
            var used = All(db, hostPaths).Select(h => h.Index).ToHashSet();
            var index = Enumerable.Range(1, MaxUsers).FirstOrDefault(i => !used.Contains(i));
            if (index == 0) throw new InvalidDataException($"All {MaxUsers} user rosters are in use.");
            db.Exec("INSERT INTO homes(name, idx, created_at) VALUES ($n,$i,$t);", ("$n", name), ("$i", index),
                ("$t", clock.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));
            home = new TenantHome(name, index, hostPaths.ForUser(name));
        }
        var p = home.Paths;
        SecureFile.EnsureDirNoClobber(hostPaths.UsersRoot);
        foreach (var d in new[] { p.Home, p.Secrets, p.BotSecretsRoot, p.Logs, p.Run, p.Bots, p.Archive, Path.Combine(p.Home, "work") }) SecureFile.EnsureDirNoClobber(d);
        if (!File.Exists(p.ConfigFile)) ConfigFor(host, hostPaths, name, home.Index).Save(p);
        return home;
    }

    /// <summary>Why a user's home cannot be served, or null. Its roster.json must still be the isolated one made for it.</summary>
    public static string? Problem(TenantHome home)
    {
        if (!File.Exists(home.Paths.ConfigFile)) return $"{home.Paths.ConfigFile} is missing.";
        RosterConfig c;
        try { c = RosterConfig.Load(home.Paths); }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException) { return $"{home.Paths.ConfigFile}: {ex.Message}"; }
        return c.Bots.Tenant == home.Id ? null : $"{home.Paths.ConfigFile} does not carry bots.tenant {home.Id}.";
    }
}

/// <summary>Opens the signed-in account's own roster for the web view in web-multi.</summary>
public static class TenantWeb
{
    public static Func<WebSession, WebDeps?> Resolver(RosterPaths hostPaths, IClock clock, WebAuth auth, WebOptions options, WebAccounts accounts, UsersDb users)
    {
        var cache = new System.Collections.Concurrent.ConcurrentDictionary<string, (WebDeps Deps, DateTime Stamp)>(StringComparer.Ordinal);
        return session =>
        {
            var name = accounts.HomeOf(session.Name);
            if (name is null || Tenants.Find(users, hostPaths, name) is not { } home || Tenants.Problem(home) is not null) return null;
            var stamp = File.GetLastWriteTimeUtc(home.Paths.ConfigFile);
            if (cache.TryGetValue(home.Name, out var hit) && hit.Stamp == stamp) return hit.Deps;
            var config = RosterConfig.Load(home.Paths);
            var db = new Db.RosterDb(home.Paths.Db);
            var audit = new Db.AuditLog(db, clock, Redactor.Shared);
            var notices = new Db.Notices(db, clock, Redactor.Shared);
            var runtime = new Runtime.AssignmentRuntime(db, config, home.Paths, clock, audit, notices,
                new Runtime.TurnDriver(db, clock, home.Paths, _ => throw new InvalidOperationException("The web view never runs turns; the user's own roster service does.")));
            var deps = new WebDeps
            {
                Db = db, Config = config, Paths = home.Paths, Clock = clock, Audit = audit, Notices = notices, Auth = auth, Options = options,
                Policy = () => new Approvals.ApproverPolicy(db, clock, config), Rooms = new Rooms.RoomService(db, clock, audit, runtime),
            };
            cache[home.Name] = (deps, stamp);
            return deps;
        };
    }
}
