// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Omabot.Common;
using Omabot.Config;
using Omabot.Db;

namespace Omabot.Egress;

/// <summary>A desktop as the egress policy sees it: its gate client name and whose allowlist it uses right now.</summary>
public sealed record DesktopClient(string Name, string? AllowFrom);

/// <summary>
/// The roster's side of the gate: one token per client (a bot agent, a bot's
/// command checks, a desktop), the proxy URL each container gets, and the
/// policy file the gate reads. Tokens live 0600 under secrets/egress; the
/// policy holds only their hashes.
/// </summary>
public sealed partial class EgressClients(RosterPaths paths, RosterConfig config, Redactor redactor)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{0,79}$")]
    private static partial Regex ClientName();

    public string Dir => Path.Combine(paths.Secrets, "egress");
    public string PolicyDir => Path.Combine(paths.Run, "egress");
    public string PolicyPath => Path.Combine(PolicyDir, "policy.json");
    public string LogDir => Path.Combine(paths.Logs, "egress");
    public string LogPath => Path.Combine(LogDir, "egress.log");

    public static string Agent(string slug) => slug;
    public static string Check(string slug) => "check." + slug;
    public static string Desktop(string name) => "desk." + name;

    public string Token(string client)
    {
        if (!ClientName().IsMatch(client)) throw new ArgumentException($"Bad egress client name {client}.");
        SecureFile.EnsureDirNoClobber(paths.Secrets);
        SecureFile.EnsureDirNoClobber(Dir);
        var file = Path.Combine(Dir, client + ".token");
        if (!File.Exists(file)) SecureFile.WriteIfMissing(file, SecureFile.NewToken() + "\n");
        var token = File.ReadAllText(file).Trim();
        redactor.AddKnownSecret(token);
        return token;
    }

    /// <summary>The proxy URL a container gets in HTTPS_PROXY and HTTP_PROXY. It carries the client's token.</summary>
    public string ProxyUrl(string client) => $"http://{client}:{Token(client)}@{config.Egress.Host}:{config.Egress.Port}";

    /// <summary>The policy for every enabled bot, its checks and every desktop.</summary>
    public EgressPolicy Build(IEnumerable<BotRow> bots, IEnumerable<DesktopClient> desktops)
    {
        var blocked = config.Egress.BlockedHosts.Select(AddressPolicy.Normalize).Distinct().OrderBy(h => h, StringComparer.Ordinal).ToList();
        var p = new EgressPolicy { Ports = [.. config.Egress.Ports.Distinct().Order()], BlockedHosts = blocked };
        var enabled = bots.Where(b => b.Enabled).ToList();
        List<string> Allow(BotRow? b) => b is null ? [] : [.. b.Spec.AllowHosts.Where(h => !AddressPolicy.HostBlocked(h, blocked.Concat(EgressPolicy.AlwaysBlocked))).Order(StringComparer.Ordinal)];
        foreach (var b in enabled.OrderBy(b => b.Slug, StringComparer.Ordinal))
        {
            if (b.Spec.IsReadOnlyRole) continue;
            p.Clients.Add(new EgressClient { Name = Agent(b.Slug), Kind = "agent", TokenSha256 = SecureFile.Sha256Hex(Token(Agent(b.Slug))), Allow = Allow(b) });
            p.Clients.Add(new EgressClient { Name = Check(b.Slug), Kind = "check", TokenSha256 = SecureFile.Sha256Hex(Token(Check(b.Slug))), Allow = Allow(b) });
        }
        foreach (var d in desktops.OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            var owner = d.AllowFrom is null ? null : enabled.FirstOrDefault(b => b.Slug == d.AllowFrom);
            p.Clients.Add(new EgressClient { Name = Desktop(d.Name), Kind = "desktop", TokenSha256 = SecureFile.Sha256Hex(Token(Desktop(d.Name))), Allow = Allow(owner) });
        }
        return p;
    }

    /// <summary>Writes the policy when it changed. The gate reloads it on its own. Returns true when the file changed.</summary>
    public bool Write(EgressPolicy policy)
    {
        SecureFile.EnsureDirNoClobber(paths.Run);
        Directory.CreateDirectory(PolicyDir);
        var json = policy.ToJson();
        if (File.Exists(PolicyPath) && File.ReadAllText(PolicyPath) == json) return false;
        SecureFile.WriteAllTextAtomic(PolicyPath, json);
        // The gate runs as another user inside its container and only reads hashes and allowlists.
        File.SetUnixFileMode(PolicyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        return true;
    }
}

/// <summary>
/// Reads the gate's decision log and raises one notice when a client is
/// refused too often (plan 7.7: 20 refusals in 10 minutes).
/// </summary>
public sealed class EgressMonitor(string logPath, RosterConfig config, IClock clock, Notices notices, AuditLog audit)
{
    private long _offset;
    private readonly Dictionary<string, Queue<DateTimeOffset>> _refusals = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Tick()
    {
        var lines = new List<string>();
        if (!File.Exists(logPath)) return lines;
        var length = new FileInfo(logPath).Length;
        if (length < _offset) _offset = 0;
        using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(_offset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        string? line;
        var window = TimeSpan.FromMinutes(config.Egress.AlarmMinutes);
        while ((line = reader.ReadLine()) is not null)
        {
            EgressLogEntry? e = null;
            try { e = System.Text.Json.JsonSerializer.Deserialize<EgressLogEntry>(line, EgressPolicy.LineJson); }
            catch (System.Text.Json.JsonException) { }
            if (e is null || e.Decision != "refuse") continue;
            var at = DateTimeOffset.TryParse(e.At, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : clock.UtcNow;
            if (!_refusals.TryGetValue(e.Client, out var q)) _refusals[e.Client] = q = new Queue<DateTimeOffset>();
            q.Enqueue(at);
            while (q.Count > 0 && at - q.Peek() > window) q.Dequeue();
            if (q.Count >= config.Egress.AlarmRefusals)
            {
                if (notices.Add("alarm", e.Client, $"The egress gate refused {e.Client} {q.Count} times in {config.Egress.AlarmMinutes} minutes (last: {e.Host}:{e.Port}, {e.Reason}). See oma-bot egress log.", "egress_refusals:" + e.Client))
                {
                    audit.Write("roster", "egress.alarm", e.Client, $"{q.Count} refusals in {config.Egress.AlarmMinutes} minutes");
                    lines.Add($"egress alarm for {e.Client}");
                }
                q.Clear();
            }
        }
        _offset = fs.Position;
        return lines;
    }
}
