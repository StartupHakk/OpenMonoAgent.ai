// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Omabot.Common;
using Omabot.Config;
using Omabot.Containers;
using Omabot.Db;
using Omabot.Gateway;
using Omabot.Render;
using Omabot.Supervision;

namespace Omabot.Doctor;

public enum CheckStatus { Ok, Warn, Fail, Skip }

public sealed record CheckResult(string Name, CheckStatus Status, string Message);

public sealed record DoctorOptions(string AppDir, bool Offline = true, ISupervisorClient? Supervisor = null, HttpClient? Http = null, HttpMessageHandler? HttpsHandler = null);

/// <summary>
/// `oma-bot doctor` (plan 7.13). Fails closed: any failed check fails the
/// run (lesson L14), and it never depends on the repo path (lesson L13).
/// Messages name files and fields but never print a secret value.
/// </summary>
public sealed class DoctorChecks(RosterPaths paths, IClock clock)
{
    public async Task<IReadOnlyList<CheckResult>> RunAsync(DoctorOptions options, CancellationToken ct = default)
    {
        var results = new List<CheckResult>();
        RosterConfig? config = null;
        try
        {
            config = RosterConfig.Load(paths);
            results.Add(new("config", CheckStatus.Ok, "roster.json is valid."));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or ArgumentException)
        {
            results.Add(new("config", CheckStatus.Fail, "roster.json: " + Redactor.Shared.Redact(ex.Message)));
            return results;
        }

        results.AddRange(FileModes());
        results.AddRange(Binds(config));
        results.Add(Upstream(config));
        var web = WebChecks(config).ToList();
        results.AddRange(web);
        if (!options.Offline && config.Mode != "local" && web.All(r => r.Status != CheckStatus.Fail)) results.Add(await WebFront(config, options, ct).ConfigureAwait(false));

        if (!File.Exists(paths.Db))
        {
            results.Add(new("roster_db", CheckStatus.Warn, "roster.db does not exist yet. Run oma-bot team apply."));
            return results;
        }
        var db = new RosterDb(paths.Db);
        var audit = new AuditLog(db, clock, Redactor.Shared);
        var broken = audit.Verify();
        results.Add(broken is null
            ? new("audit_chain", CheckStatus.Ok, "Audit hash chain is intact.")
            : new("audit_chain", CheckStatus.Fail, $"Audit hash chain broken at row {broken.Value.Id}: {broken.Value.Reason}"));

        var bots = new BotStore(db, clock).All();
        results.AddRange(ContainerSpecs(config, bots, options));
        results.AddRange(Rendered(config, bots, options));
        results.Add(SecretScan(bots));
        results.Add(HealthBotTools(config, bots));

        if (options.Supervisor is not null)
        {
            var up = await options.Supervisor.PingAsync(ct).ConfigureAwait(false);
            results.Add(up ? new("supervisor", CheckStatus.Ok, "Supervisor answers.") : new("supervisor", CheckStatus.Warn, "Supervisor is not running. Start it with oma-bot supervisor (or the systemd unit)."));
            if (up)
            {
                var image = Supervision.BotLauncher.ImageProblem(config, await options.Supervisor.ImageIdAsync(config.Bots.Image, ct).ConfigureAwait(false));
                results.Add(image is null ? new("agent_image", CheckStatus.Ok, $"{config.Bots.Image} is the accepted image.") : new("agent_image", CheckStatus.Fail, image));
            }
        }
        if (!options.Offline && options.Http is not null) results.Add(await Inference(config, options.Http, ct).ConfigureAwait(false));
        return results;
    }

    private IEnumerable<CheckResult> FileModes()
    {
        if (!SecureFile.IsUnix) yield break;
        foreach (var (name, path, dir) in new[] { ("home", paths.Home, true), ("secrets_dir", paths.Secrets, true), ("roster_db", paths.Db, false), ("config_file", paths.ConfigFile, false) })
        {
            if (!(dir ? Directory.Exists(path) : File.Exists(path))) continue;
            yield return SecureFile.IsPrivate(path)
                ? new($"mode_{name}", CheckStatus.Ok, $"{path} is private.")
                : new($"mode_{name}", CheckStatus.Fail, $"{path} is readable by others. Run chmod {(dir ? "700" : "600")} {path}.");
        }
        if (Directory.Exists(paths.Secrets))
        {
            var loose = Directory.EnumerateFiles(paths.Secrets, "*", SearchOption.AllDirectories).Where(f => !SecureFile.IsPrivate(f)).ToList();
            yield return loose.Count == 0
                ? new("mode_secret_files", CheckStatus.Ok, "All secret files are 0600.")
                : new("mode_secret_files", CheckStatus.Fail, $"{loose.Count} secret file(s) are readable by others, first: {loose[0]}.");
        }
    }

    private static IEnumerable<CheckResult> Binds(RosterConfig config)
    {
        yield return System.Net.IPAddress.TryParse(config.Api.BindAddress, out var apiIp) && System.Net.IPAddress.IsLoopback(apiIp)
            ? new("bind_api", CheckStatus.Ok, $"Roster API binds {config.Api.BindAddress}.")
            : new("bind_api", CheckStatus.Fail, $"Roster API binds {config.Api.BindAddress}. It must bind a loopback address; web modes reach it through the HTTPS front.");
        yield return config.Gateway.BindAddress is "0.0.0.0" or "::" or "*"
            ? new("bind_gateway", CheckStatus.Fail, "Gateway binds every address. Bind the docker bridge address (172.17.0.1) only.")
            : new("bind_gateway", CheckStatus.Ok, $"Gateway binds {config.Gateway.BindAddress}.");
    }

    /// <summary>Web mode needs HTTPS through the front, a built login method, the admin secret and an owner (plan 7.20 item 1).</summary>
    private IEnumerable<CheckResult> WebChecks(RosterConfig config)
    {
        if (config.Mode == "local")
        {
            if (File.Exists(paths.UsersDb) && SecureFile.IsUnix)
                yield return SecureFile.IsPrivate(paths.UsersDb) && SecureFile.IsPrivate(paths.Web)
                    ? new("web_users_db", CheckStatus.Ok, $"{paths.UsersDb} is private.")
                    : new("web_users_db", CheckStatus.Fail, $"{paths.UsersDb} or its folder is readable by others. Run chmod 700 {paths.Web} and chmod 600 {paths.UsersDb}.");
            yield break;
        }
        var problems = Web.WebMode.Problems(config, paths);
        if (problems.Count == 0) yield return new("web_mode", CheckStatus.Ok, $"Web mode {config.Mode} for {config.Web.PublicOrigin}: HTTPS front, login, admin secret and an owner are in place.");
        var n = 0;
        foreach (var p in problems) yield return new($"web_mode_{++n}", CheckStatus.Fail, p);
    }

    private async Task<CheckResult> WebFront(RosterConfig config, DoctorOptions options, CancellationToken ct)
    {
        var nonce = Web.WebMode.NewNonce();
        SecureFile.WriteAllTextAtomic(paths.WebCheckNonce, nonce);
        try
        {
            var problem = await Web.WebMode.FrontProblemAsync(config, nonce, options.HttpsHandler, ct).ConfigureAwait(false);
            return problem is null
                ? new("web_front", CheckStatus.Ok, $"{config.Web.PublicOrigin} has a valid certificate and reaches this roster.")
                : new("web_front", CheckStatus.Fail, problem);
        }
        finally
        {
            File.Delete(paths.WebCheckNonce);
        }
    }

    private static CheckResult Upstream(RosterConfig config)
    {
        var endpoints = new List<string?> { config.Gateway.Upstream };
        endpoints.AddRange(config.Gateway.Backends.Select(b => (string?)b.Endpoint));
        endpoints = [.. endpoints.Where(e => !string.IsNullOrWhiteSpace(e))];
        if (endpoints.Count == 0) return new("upstream", CheckStatus.Warn, "No inference upstream set yet. Set gateway.upstream to the relay address.");
        foreach (var e in endpoints)
        {
            var problem = UpstreamPolicy.Check(e, config.Gateway.AllowPlainHttpUpstream);
            if (problem is not null) return new("upstream", CheckStatus.Fail, problem);
        }
        if (config.Gateway.AllowPlainHttpUpstream && endpoints.Any(e => e!.StartsWith("http:", StringComparison.Ordinal) && !UpstreamPolicy.IsLoopback(new Uri(e!).Host)))
            return new("upstream", CheckStatus.Warn, "allow_plain_http_upstream is on: inference traffic is not encrypted between the gateway and the relay.");
        return new("upstream", CheckStatus.Ok, "Upstream address passes the address policy.");
    }

    private IEnumerable<CheckResult> ContainerSpecs(RosterConfig config, IReadOnlyList<BotRow> bots, DoctorOptions options)
    {
        var renderer = new BotRenderer(paths, config);
        var problems = new List<string>();
        var dataDirs = new Dictionary<string, string>(StringComparer.Ordinal);
        var workspaces = new Dictionary<string, List<BotRow>>(StringComparer.Ordinal);
        var ports = new HashSet<int>();
        foreach (var b in bots)
        {
            var spec = ContainerSpec.For(b.Spec, b.Port, "omb_doctor_placeholder_token", paths, config, renderer, options.AppDir, false);
            foreach (var p in spec.SafetyProblems()) problems.Add($"{b.Name}: {p}");
            var data = paths.Bot(b.Slug).Data;
            if (dataDirs.TryGetValue(data, out var other)) problems.Add($"{b.Name} and {other} share a data dir");
            dataDirs[data] = b.Name;
            var ws = renderer.WorkspaceOf(b.Spec);
            if (!workspaces.TryGetValue(ws, out var list)) workspaces[ws] = list = [];
            list.Add(b);
            if (!ports.Add(b.Port)) problems.Add($"{b.Name}: port {b.Port} is used twice");
        }
        foreach (var (ws, list) in workspaces)
            if (list.Count > 1 && !list.All(b => b.Spec.SharedWorkspace))
                problems.Add($"workspace {ws} is shared by {string.Join(", ", list.Select(b => b.Name))} without shared_workspace on all of them");
        yield return problems.Count == 0
            ? new("container_specs", CheckStatus.Ok, $"{bots.Count} bot container spec(s): loopback ports, no docker.sock, shadowed host bridge, distinct mounts.")
            : new("container_specs", CheckStatus.Fail, string.Join("; ", problems));
    }

    private IEnumerable<CheckResult> Rendered(RosterConfig config, IReadOnlyList<BotRow> bots, DoctorOptions options)
    {
        var shimInstalled = File.Exists(Path.Combine(options.AppDir, "mcp-shim", "run"));
        var renderer = new BotRenderer(paths, config);
        foreach (var b in bots)
        {
            var bp = paths.Bot(b.Slug);
            if (!File.Exists(bp.Settings)) continue;
            var settings = File.ReadAllText(bp.Settings);
            if (settings.Contains(BotRenderer.ShimMount, StringComparison.Ordinal) && !shimInstalled)
                yield return new($"mcp_shim_{b.Slug}", CheckStatus.Fail, $"{b.Name} settings name the MCP shim but {Path.Combine(options.AppDir, "mcp-shim", "run")} is missing.");
            if (settings.Contains("\"127.0.0.1", StringComparison.Ordinal) || settings.Contains("localhost", StringComparison.OrdinalIgnoreCase))
                yield return new($"gateway_address_{b.Slug}", CheckStatus.Fail, $"{b.Name} settings point at loopback. Inside a container that is the container itself.");
            if (!File.Exists(bp.SystemPrompt) || !File.ReadAllText(bp.SystemPrompt).Contains("## Hard rules", StringComparison.Ordinal))
                yield return new($"hard_rules_{b.Slug}", CheckStatus.Fail, $"{b.Name} has no hard rules in its data dir system prompt.");
            var omd = Path.Combine(renderer.WorkspaceOf(b.Spec), "OPENMONO.md");
            if (b.RenderedHash is not null && File.Exists(omd) && SecureFile.HashOfFile(omd) != b.RenderedHash)
                yield return new($"openmono_md_{b.Slug}", CheckStatus.Warn, $"{omd} changed since the roster rendered it. The next start rewrites it.");
        }
    }

    /// <summary>Looks for known secret values (bot tokens, inference key) and key shaped strings in logs and memory files.</summary>
    private CheckResult SecretScan(IReadOnlyList<BotRow> bots)
    {
        var secrets = new List<string>();
        if (Directory.Exists(paths.Secrets))
            foreach (var f in Directory.EnumerateFiles(paths.Secrets, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(f);
                if (info.Length is > 0 and < 8192)
                    foreach (var line in File.ReadAllLines(f).Select(l => l.Trim()).Where(l => l.Length >= 12)) secrets.Add(line);
            }
        var files = new List<string>();
        if (Directory.Exists(paths.Logs)) files.AddRange(Directory.EnumerateFiles(paths.Logs, "*", SearchOption.AllDirectories));
        foreach (var b in bots)
        {
            var bp = paths.Bot(b.Slug);
            var ws = b.Spec.Workspace == "auto" ? bp.DefaultWorkspace : b.Spec.Workspace;
            foreach (var dir in new[] { Path.Combine(bp.Data, "memory"), Path.Combine(ws, "memory"), bp.Journal })
                if (Directory.Exists(dir)) files.AddRange(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories));
            if (File.Exists(Path.Combine(ws, "MEMORY.md"))) files.Add(Path.Combine(ws, "MEMORY.md"));
        }
        var hits = new List<string>();
        foreach (var f in files)
        {
            if (new FileInfo(f).Length > 20_000_000) continue;
            var text = File.ReadAllText(f);
            if (secrets.Any(s => text.Contains(s, StringComparison.Ordinal)) || Redactor.ContainsStrongSecret(text)) hits.Add(f);
        }
        return hits.Count == 0
            ? new("secret_scan", CheckStatus.Ok, $"Scanned {files.Count} log and memory file(s); no secret found.")
            : new("secret_scan", CheckStatus.Fail, $"A secret value was found in {hits.Count} file(s), first: {hits[0]}. Rotate the secret and clean the file.");
    }

    private CheckResult HealthBotTools(RosterConfig config, IReadOnlyList<BotRow> bots)
    {
        var problems = new List<string>();
        foreach (var b in bots.Where(b => b.Spec.IsReadOnlyRole))
        {
            foreach (var t in b.Spec.ToolsAllow.Where(t => !Team.ToolCatalog.IsReadOnly(t))) problems.Add($"{b.Name} has write tool {t}");
            if (b.Spec.HostExec) problems.Add($"{b.Name} has host_exec");
            var bp = paths.Bot(b.Slug);
            if (File.Exists(bp.Settings))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(bp.Settings));
                if (doc.RootElement.TryGetProperty("permissions", out var perms) && perms.TryGetProperty("tools", out var tools))
                    foreach (var tool in tools.EnumerateObject())
                        if (!Team.ToolCatalog.IsReadOnly(tool.Name) && tool.Value.GetProperty("allow").GetArrayLength() > 0)
                            problems.Add($"{b.Name} rendered settings allow {tool.Name}");
            }
        }
        _ = config;
        return problems.Count == 0
            ? new("health_bot_read_only", CheckStatus.Ok, "Health bot has read-only tools only.")
            : new("health_bot_read_only", CheckStatus.Fail, string.Join("; ", problems));
    }

    private async Task<CheckResult> Inference(RosterConfig config, HttpClient http, CancellationToken ct)
    {
        var endpoint = config.Gateway.Upstream ?? config.Gateway.Backends.FirstOrDefault()?.Endpoint;
        if (endpoint is null) return new("inference", CheckStatus.Warn, "No upstream to probe.");
        if (UpstreamPolicy.Check(endpoint, config.Gateway.AllowPlainHttpUpstream) is { } p) return new("inference", CheckStatus.Fail, p);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, endpoint.TrimEnd('/') + "/models");
            var keyPath = Path.IsPathRooted(config.Gateway.UpstreamKeyFile) ? config.Gateway.UpstreamKeyFile : Path.Combine(paths.Home, config.Gateway.UpstreamKeyFile);
            if (File.Exists(keyPath)) req.Headers.Authorization = new("Bearer", File.ReadAllText(keyPath).Trim());
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            using var r = await http.SendAsync(req, cts.Token).ConfigureAwait(false);
            return r.IsSuccessStatusCode
                ? new("inference", CheckStatus.Ok, "Inference answers through the relay.")
                : new("inference", CheckStatus.Fail, $"Inference answered {(int)r.StatusCode} through the relay.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new("inference", CheckStatus.Fail, "Inference unreachable through the relay: " + Redactor.Shared.Redact(ex.Message));
        }
    }

    public static int ExitCode(IEnumerable<CheckResult> results) => results.Any(r => r.Status == CheckStatus.Fail) ? 1 : 0;

    public static string Format(IEnumerable<CheckResult> results, bool quiet)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var r in results)
        {
            if (quiet && r.Status is CheckStatus.Ok or CheckStatus.Skip) continue;
            sb.Append(r.Status switch { CheckStatus.Ok => "ok   ", CheckStatus.Warn => "warn ", CheckStatus.Fail => "FAIL ", _ => "skip " })
              .Append(r.Name).Append(": ").Append(r.Message).Append('\n');
        }
        return sb.ToString();
    }

    public static string ToJson(IEnumerable<CheckResult> results)
        => JsonSerializer.Serialize(results.Select(r => new { name = r.Name, status = r.Status.ToString().ToLowerInvariant(), message = r.Message }));
}
