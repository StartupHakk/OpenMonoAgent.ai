// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Omabot.Common;
using Omabot.Db;
using Omabot.Egress;
using Omabot.Team;

namespace Omabot.Routines;

/// <summary>What a check found. Changed means the routine needs the model; Output goes into the turn.</summary>
public sealed record CheckOutcome(bool Changed, string Output, string Fingerprint, string? Error)
{
    public static CheckOutcome Fail(string error) => new(false, "", "", error);
}

/// <summary>Runs a script from bot/checks in a throwaway container (through the supervisor).</summary>
public interface ICommandCheckRunner
{
    Task<CommandCheckResult> RunAsync(string botSlug, string script, IReadOnlyList<string> args, string workspace, bool network, TimeSpan timeout, CancellationToken ct);
}

public sealed record CommandCheckResult(long? ExitCode, string Output, string? Error);

/// <summary>Everything checks need from the roster. Tests swap the resolver, HTTP handler and runners.</summary>
public sealed class CheckEnvironment
{
    public required RosterPaths Paths { get; init; }
    public required IClock Clock { get; init; }
    public required RosterDb Db { get; init; }
    public required string AppDir { get; init; }
    public IReadOnlyList<string> WorkspaceRoots { get; init; } = [];
    public IReadOnlyList<string> BlockedHosts { get; init; } = [];
    public int OutputCap { get; init; } = 4096;
    public ICommandCheckRunner? Commands { get; init; }
    public Func<CancellationToken, Task<IReadOnlyList<Doctor.CheckResult>>>? Doctor { get; init; }
    public IHostResolver Resolver { get; init; } = DnsResolver.Instance;
    /// <summary>Opens a TCP stream to an already checked address. Tests replace it.</summary>
    public Func<IPAddress, int, CancellationToken, ValueTask<Stream>>? Connect { get; init; }
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Routine checks (plan 7.4): plain code that runs before any model call.
/// If every check says "no change", the run is quiet and no model call is
/// made. Fingerprints are compared with the last recorded run.
/// </summary>
public static partial class Checks
{
    public static readonly string[] Kinds = ["file_changed", "git_new_commits", "http_status", "command", "assignments_waiting", "doctor", "always"];

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex ScriptName();

    [GeneratedRegex("^[A-Za-z0-9._/=:,+@-]{1,200}$")]
    private static partial Regex ScriptArg();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/-]{0,120}$")]
    private static partial Regex BranchName();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}(\\.[A-Za-z_][A-Za-z0-9_]{0,63}){0,7}$")]
    private static partial Regex FieldPath();

    [GeneratedRegex("^[0-9a-f]{40}([0-9a-f]{24})?$")]
    private static partial Regex Sha();

    public static bool IsScriptName(string s) => ScriptName().IsMatch(s) && !s.Contains("..", StringComparison.Ordinal);

    private static readonly Dictionary<string, string[]> AllowedArgs = new(StringComparer.Ordinal)
    {
        ["file_changed"] = ["path"],
        ["git_new_commits"] = ["repo", "branch", "fetch"],
        ["http_status"] = ["url", "field"],
        ["command"] = ["run", "network"],
        ["assignments_waiting"] = [],
        ["doctor"] = [],
        ["always"] = [],
    };

    /// <summary>Shape problems with a check, or null. Used by team apply and the routine tools.</summary>
    public static string? Problem(CheckDef c)
    {
        if (!AllowedArgs.TryGetValue(c.Kind, out var allowed)) return $"unknown check kind (use {string.Join(", ", Kinds)}).";
        foreach (var k in c.Args.Keys)
            if (Array.IndexOf(allowed, k) < 0) return $"unknown argument \"{k}\".";
        string A(string k) => c.Args.GetValueOrDefault(k) ?? "";
        switch (c.Kind)
        {
            case "file_changed":
                if (A("path").Length == 0) return "path is required.";
                if (!TeamParser.SafeRelativeOrAbsolute(A("path")) || Path.IsPathRooted(A("path"))) return "path is relative to the bot workspace, without ~, .. or . segments.";
                return null;
            case "git_new_commits":
                if (A("repo").Length == 0 || A("branch").Length == 0) return "repo and branch are required.";
                if (!TeamParser.SafeRelativeOrAbsolute(A("repo"))) return "repo may not use ~, .. or . segments.";
                if (!BranchName().IsMatch(A("branch")) || A("branch").Contains("..", StringComparison.Ordinal)) return "branch is not a plain branch name.";
                if (A("fetch") is not ("" or "true" or "false")) return "fetch is true or false.";
                return null;
            case "http_status":
                if (!Uri.TryCreate(A("url"), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return "url must be an absolute http or https address.";
                if (!string.IsNullOrEmpty(u.UserInfo)) return "url may not carry credentials.";
                if (IPAddress.TryParse(u.Host.Trim('[', ']'), out _)) return "url must use a host name on the allowlist, not an IP address.";
                if (A("field").Length > 0 && !FieldPath().IsMatch(A("field"))) return "field is a dotted JSON path like status.state.";
                return null;
            case "command":
            {
                var parts = A("run").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return "run names a script in bot/checks.";
                if (!IsScriptName(parts[0])) return "run names a script in bot/checks (letters, digits, dot, dash, underscore).";
                if (parts.Skip(1).Any(p => !ScriptArg().IsMatch(p))) return "script arguments may use letters, digits and ._/=:,+@- only.";
                if (A("network") is not ("" or "true" or "false")) return "network is true or false.";
                return null;
            }
            default:
                return null;
        }
    }

    /// <summary>Runs one check. Never throws; a failure is reported in <see cref="CheckOutcome.Error"/>.</summary>
    public static async Task<CheckOutcome> RunAsync(CheckDef c, BotSpec bot, string workspace, string? previous, CheckEnvironment env, CancellationToken ct)
    {
        try
        {
            if (Problem(c) is { } p) return CheckOutcome.Fail(p);
            var outcome = c.Kind switch
            {
                "file_changed" => FileChanged(c, workspace, previous),
                "git_new_commits" => await GitNewCommitsAsync(c, bot, workspace, previous, env, ct).ConfigureAwait(false),
                "http_status" => await HttpStatusAsync(c, bot, previous, env, ct).ConfigureAwait(false),
                "command" => await CommandAsync(c, bot, workspace, env, ct).ConfigureAwait(false),
                "assignments_waiting" => AssignmentsWaiting(bot, env),
                "doctor" => await DoctorAsync(previous, env, ct).ConfigureAwait(false),
                _ => new CheckOutcome(true, "", "always", null),
            };
            return outcome with { Output = Cap(Redactor.Shared.Redact(outcome.Output), env.OutputCap), Error = outcome.Error is null ? null : Redactor.Shared.Redact(outcome.Error) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or SocketException or JsonException or InvalidOperationException)
        {
            return CheckOutcome.Fail(Redactor.Shared.Redact(ex.Message));
        }
    }

    public static string Cap(string text, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            if (Encoding.UTF8.GetByteCount(sb.ToString()) + 64 >= maxBytes) break;
            sb.Append(ch);
        }
        return sb.Append("\n(cut at ").Append(maxBytes.ToString(CultureInfo.InvariantCulture)).Append(" bytes)").ToString();
    }

    private static bool Changed(string? previous, string fingerprint) => previous is not null && previous != fingerprint;

    // ---------- file_changed ----------

    private static CheckOutcome FileChanged(CheckDef c, string workspace, string? previous)
    {
        var rel = c.Args["path"];
        var full = Path.GetFullPath(Path.Combine(workspace, rel));
        var root = Path.GetFullPath(workspace).TrimEnd('/');
        if (full != root && !full.StartsWith(root + "/", StringComparison.Ordinal)) return CheckOutcome.Fail("path leaves the workspace.");
        if (SecureFile.HasSymlinkSegment(full)) return CheckOutcome.Fail("path has a symlink, which is refused.");
        string fp;
        string what;
        if (File.Exists(full))
        {
            using var s = File.OpenRead(full);
            fp = "file:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(s));
            var fi = new FileInfo(full);
            what = $"{rel} changed (size {fi.Length.ToString(CultureInfo.InvariantCulture)} bytes, modified {fi.LastWriteTimeUtc:yyyy-MM-dd HH:mm} UTC).";
        }
        else if (Directory.Exists(full))
        {
            var lines = new List<string>();
            var count = 0;
            foreach (var f in Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
            {
                if (++count > 5000) break;
                var fi = new FileInfo(f);
                lines.Add($"{Path.GetRelativePath(full, f)}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}");
            }
            lines.Sort(StringComparer.Ordinal);
            fp = "dir:" + SecureFile.Sha256Hex(string.Join('\n', lines));
            what = $"Something in {rel}/ changed ({count.ToString(CultureInfo.InvariantCulture)} file(s) now).";
        }
        else
        {
            fp = "absent";
            what = $"{rel} was removed.";
        }
        var changed = Changed(previous, fp);
        if (changed && previous == "absent") what = $"{rel} appeared.";
        return new CheckOutcome(changed, changed ? what : "", fp, null);
    }

    // ---------- git_new_commits ----------

    private static async Task<CheckOutcome> GitNewCommitsAsync(CheckDef c, BotSpec bot, string workspace, string? previous, CheckEnvironment env, CancellationToken ct)
    {
        var repoArg = c.Args["repo"];
        var branch = c.Args["branch"];
        var repo = Path.IsPathRooted(repoArg) ? Path.GetFullPath(repoArg) : Path.GetFullPath(Path.Combine(workspace, repoArg));
        var allowedRoots = env.WorkspaceRoots.Append(workspace).ToList();
        if (!allowedRoots.Any(r => TeamResolver.IsUnder(repo, r))) return CheckOutcome.Fail("repo is outside the bot workspace and the allowed workspace roots.");
        if (SecureFile.HasSymlinkSegment(repo)) return CheckOutcome.Fail("repo path has a symlink, which is refused.");
        var gitDir = Path.Combine(repo, ".git");
        if (File.Exists(gitDir)) return CheckOutcome.Fail("repo uses a .git file (worktree or submodule); point the check at the main clone.");
        if (!Directory.Exists(gitDir)) gitDir = repo; // a bare clone
        if (!File.Exists(Path.Combine(gitDir, "HEAD"))) return CheckOutcome.Fail("repo is not a git clone.");
        var refName = $"refs/heads/{branch}";
        if (c.Args.GetValueOrDefault("fetch") == "true")
        {
            // Fetching runs git, so it runs in a throwaway container, never on the host.
            if (env.Commands is null) return CheckOutcome.Fail("fetch needs the supervisor (Docker) to run git in a throwaway container.");
            var r = await env.Commands.RunAsync(bot.Slug, "git-fetch.sh", [branch], repo, network: true, env.CommandTimeout, ct).ConfigureAwait(false);
            if (r.Error is not null || r.ExitCode != 0) return CheckOutcome.Fail("git fetch failed: " + (r.Error ?? r.Output.Trim()));
            refName = $"refs/remotes/origin/{branch}";
        }
        var head = ReadRef(gitDir, refName);
        if (head is null) return CheckOutcome.Fail($"branch {branch} not found in {repoArg}.");
        var changed = previous is not null && previous != head;
        var output = changed && previous is not null ? $"{repoArg} {branch} moved from {previous[..Math.Min(12, previous.Length)]} to {head[..12]}. Read the repository for details." : "";
        return new CheckOutcome(changed, output, head, null);
    }

    /// <summary>Reads a ref from loose files or packed-refs. No git process runs, so a repository config cannot run commands.</summary>
    internal static string? ReadRef(string gitDir, string refName)
    {
        var loose = Path.Combine(gitDir, refName.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(loose) && new FileInfo(loose).LinkTarget is null)
        {
            var v = File.ReadAllText(loose).Trim();
            if (Sha().IsMatch(v)) return v;
        }
        var packed = Path.Combine(gitDir, "packed-refs");
        if (!File.Exists(packed) || new FileInfo(packed).LinkTarget is not null) return null;
        foreach (var line in File.ReadLines(packed))
        {
            if (line.StartsWith('#') || line.StartsWith('^')) continue;
            var parts = line.Split(' ', 2);
            if (parts.Length == 2 && parts[1].Trim() == refName && Sha().IsMatch(parts[0])) return parts[0];
        }
        return null;
    }

    // ---------- http_status ----------

    private static async Task<CheckOutcome> HttpStatusAsync(CheckDef c, BotSpec bot, string? previous, CheckEnvironment env, CancellationToken ct)
    {
        var url = new Uri(c.Args["url"]);
        var host = AddressPolicy.Normalize(url.Host);
        if (AddressPolicy.HostBlocked(host, env.BlockedHosts)) return CheckOutcome.Fail($"{host} is a blocked host.");
        if (!AddressPolicy.HostAllowed(host, bot.AllowHosts)) return CheckOutcome.Fail($"{host} is not on the bot's web.allow_hosts.");
        var pick = await SafeResolve.PickAsync(env.Resolver, host, ct).ConfigureAwait(false);
        if (pick.Address is null) return CheckOutcome.Fail($"{host} refused: {pick.Refusal}.");
        var address = pick.Address;
        var port = url.Port;
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = async (_, token) =>
            {
                if (env.Connect is not null) return await env.Connect(address, port, token).ConfigureAwait(false);
                var s = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await s.ConnectAsync(new IPEndPoint(address, port), token).ConfigureAwait(false);
                    return new NetworkStream(s, ownsSocket: true);
                }
                catch
                {
                    s.Dispose();
                    throw;
                }
            },
        };
        using var http = new HttpClient(handler) { Timeout = env.HttpTimeout };
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("oma-bot-check/1");
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var code = (int)resp.StatusCode;
        var fp = "status:" + code.ToString(CultureInfo.InvariantCulture);
        string? fieldValue = null;
        if (c.Args.GetValueOrDefault("field") is { Length: > 0 } field)
        {
            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buf = new byte[64 * 1024];
            var n = 0;
            int read;
            while (n < buf.Length && (read = await body.ReadAsync(buf.AsMemory(n), ct).ConfigureAwait(false)) > 0) n += read;
            fieldValue = ReadField(Encoding.UTF8.GetString(buf, 0, n), field) ?? "(missing)";
            fp += "|" + field + "=" + fieldValue;
        }
        var changed = Changed(previous, fp);
        var output = changed
            ? $"GET {url} now answers {code.ToString(CultureInfo.InvariantCulture)}{(fieldValue is null ? "" : $", {c.Args["field"]} = {fieldValue}")} (was {previous})."
            : "";
        return new CheckOutcome(changed, output, fp, null);
    }

    private static string? ReadField(string json, string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var el = doc.RootElement;
            foreach (var part in path.Split('.'))
            {
                if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(part, out el)) return null;
            }
            var text = el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : el.GetRawText();
            return text.Length > 200 ? text[..200] : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---------- command ----------

    private static async Task<CheckOutcome> CommandAsync(CheckDef c, BotSpec bot, string workspace, CheckEnvironment env, CancellationToken ct)
    {
        var parts = c.Args["run"].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var script = Path.Combine(env.AppDir, "checks", parts[0]);
        if (!File.Exists(script) || new FileInfo(script).LinkTarget is not null) return CheckOutcome.Fail($"no check script named {parts[0]} in {Path.Combine(env.AppDir, "checks")}.");
        if (env.Commands is null) return CheckOutcome.Fail("command checks run in a throwaway container and need the supervisor (Docker).");
        var network = c.Args.GetValueOrDefault("network") == "true";
        var r = await env.Commands.RunAsync(bot.Slug, parts[0], parts[1..], workspace, network, env.CommandTimeout, ct).ConfigureAwait(false);
        if (r.Error is not null) return CheckOutcome.Fail(r.Error);
        var output = r.Output.Trim();
        return r.ExitCode switch
        {
            0 when output.Length > 0 => new CheckOutcome(true, output, "out:" + SecureFile.Sha256Hex(output), null),
            0 or 1 => new CheckOutcome(false, "", "quiet", null),
            _ => CheckOutcome.Fail($"{parts[0]} exited with code {r.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}: {Cap(output, 400)}"),
        };
    }

    // ---------- assignments_waiting ----------

    private static CheckOutcome AssignmentsWaiting(BotSpec bot, CheckEnvironment env)
    {
        var hasInbox = Convert.ToInt64(env.Db.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='inbox';") ?? 0L) > 0;
        if (!hasInbox) return new CheckOutcome(false, "", "inbox:0", null);
        var n = Convert.ToInt64(env.Db.Scalar("SELECT COUNT(*) FROM inbox WHERE bot=$b AND delivered_at IS NULL;", ("$b", bot.Slug)) ?? 0L);
        return new CheckOutcome(n > 0, n > 0 ? $"{n.ToString(CultureInfo.InvariantCulture)} inbox item(s) are waiting." : "", "inbox:" + n.ToString(CultureInfo.InvariantCulture), null);
    }

    // ---------- doctor ----------

    private static async Task<CheckOutcome> DoctorAsync(string? previous, CheckEnvironment env, CancellationToken ct)
    {
        if (env.Doctor is null) return CheckOutcome.Fail("the doctor check is not available in this process.");
        var results = await env.Doctor(ct).ConfigureAwait(false);
        var failed = results.Where(r => r.Status == Doctor.CheckStatus.Fail).Select(r => $"{r.Name}: {r.Message}").OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (failed.Count == 0) return new CheckOutcome(false, "", "healthy", null);
        var fp = "fail:" + SecureFile.Sha256Hex(string.Join('\n', failed));
        // A new set of failures is a change; the same failures again stay quiet.
        var changed = previous != fp;
        return new CheckOutcome(changed, changed ? string.Join('\n', failed) : "", fp, null);
    }
}
