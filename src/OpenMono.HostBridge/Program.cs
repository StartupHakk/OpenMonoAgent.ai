using OpenMono.HostBridge;

var options = BridgeOptions.Parse(args);
if (options.ShowHelp)
{
    BridgeOptions.PrintHelp();
    return 0;
}
if (options.ShowVersion)
{
    Console.WriteLine($"host-bridge {BridgeOptions.Version} (OpenMono.ai server sub-agent)");
    return 0;
}

var workDir = Path.GetFullPath(options.WorkDir ?? Directory.GetCurrentDirectory());
var config = BridgeConfig.Load(options.ConfigPath, Console.Error);
if (BridgeConfig.WriteDefaultIfMissing(options.ConfigPath, Console.Error))
    config = BridgeConfig.Load(options.ConfigPath, Console.Error);
BridgeConfig.MigrateStockAskToAllow(options.ConfigPath, config, Console.Error);

var interactive = !options.NonInteractive && !Console.IsInputRedirected;

// ── Run-as identity (requirement 1): the username is ordinary config; the
// password lives only in memory (prompted or via --password-file /
// OPENMONO_HOST_PASSWORD_FILE), never in settings, logs, or the repo.
var runAs = options.RunAs
    ?? Environment.GetEnvironmentVariable("OPENMONO_HOST_RUN_AS")
    ?? config.RunAs ?? "";
runAs = runAs.Trim();
var passwordFile = options.PasswordFile
    ?? Environment.GetEnvironmentVariable("OPENMONO_HOST_PASSWORD_FILE");
char[]? password = null;
if (!string.IsNullOrWhiteSpace(passwordFile))
{
    try
    {
        password = HostIdentity.ReadPasswordFile(passwordFile.Trim());
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[bridge] ERROR: {ex.Message}");
        return 1;
    }
}

// ── Sudo opt-in (requirement 2): never granted silently. Explicit flags win;
// otherwise the installer (--init) or the first run asks once and persists.
// Non-interactive without an explicit choice fails closed (no sudo).
bool? allowSudo = options.AllowSudoOverride ?? config.AllowSudo;
var sudoExplicit = options.AllowSudoOverride.HasValue;
if (options.Init || allowSudo is null)
{
    if (interactive)
    {
        if (options.RunAs is null &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENMONO_HOST_RUN_AS")))
        {
            Console.Error.Write($"Run host commands as user [{(string.IsNullOrWhiteSpace(runAs) ? HostIdentity.CurrentUser : runAs)}]: ");
            var typed = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(typed))
                runAs = typed.Trim();
        }
        if (!sudoExplicit)
        {
            Console.Error.Write("Allow this sub-agent to use sudo for host commands that need root? [y/N] ");
            var answer = Console.ReadLine();
            allowSudo = answer is not null &&
                (answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) ||
                 answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));
            sudoExplicit = true;
        }
        config.RunAs = runAs;
        config.AllowSudo = allowSudo;
        BridgeConfig.Save(options.ConfigPath, config);
        Console.Error.WriteLine(
            $"[bridge] saved identity: run_as={(string.IsNullOrWhiteSpace(runAs) ? HostIdentity.CurrentUser : runAs)}, " +
            $"allow_sudo={allowSudo!.Value.ToString().ToLowerInvariant()}");
        if (options.Init)
            return 0;
    }
    else
    {
        if (options.Init)
        {
            // Fail closed when nothing can be asked: persist only an explicit
            // choice, never an assumed Yes.
            config.RunAs = runAs;
            if (sudoExplicit)
                config.AllowSudo = allowSudo;
            else
                Console.Error.WriteLine("[bridge] --init non-interactive: leaving allow_sudo unasked (first run will ask).");
            BridgeConfig.Save(options.ConfigPath, config);
            return 0;
        }
        allowSudo ??= false;
    }
}
else if (options.Init)
{
    config.RunAs = runAs;
    BridgeConfig.Save(options.ConfigPath, config);
    Console.Error.WriteLine("[bridge] saved identity (sudo choice unchanged).");
    return 0;
}

using HostIdentity identity = new(runAs, allowSudo ?? false, password);
password = null; // owned by identity now (zeroed on dispose)

// Fail fast on the wrong-user trap: HOST_EXEC as another user needs sudo,
// and without it every host command would fail one by one mid-session.
if (!identity.RunAsUser.Equals(HostIdentity.CurrentUser, StringComparison.Ordinal) && !identity.AllowSudo)
{
    Console.Error.WriteLine(
        $"[bridge] ERROR: run_as='{identity.RunAsUser}' but you are '{HostIdentity.CurrentUser}' and allow_sudo=false. " +
        "Host commands would fail. Run the bridge as that user, re-run --init to change run_as, or enable sudo.");
    return 1;
}

// Full-screen agent TUI on a real terminal (same renderer as openmono agent);
// plain lines when piped, non-interactive, or --classic.
var useTui = !options.Classic &&
    (options.ForceTui || (interactive && !Console.IsOutputRedirected));
if (options.ForceTui && !useTui && options.Classic)
{
    Console.Error.WriteLine("[bridge] WARNING: --tui and --classic conflict; --classic wins.");
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var executor = new HostExecutor(
    config.HostExec, workDir, config.ResolvedLogDir, identity);
using var acp = new AcpClient();

string baseUrl;
var container = null as AgentContainer;
var weSpawned = false;
try
{
    (baseUrl, container, weSpawned) = await ResolveAgentAsync(options, config, workDir, cts.Token);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[bridge] ERROR: {ex.Message}");
    return 1;
}

try
{
    string sessionId;
    var freshSession = false;
    try
    {
        if (!string.IsNullOrWhiteSpace(options.SessionId))
        {
            await acp.EnsureSessionAsync(baseUrl, options.SessionId, cts.Token);
            sessionId = options.SessionId;
        }
        else
        {
            sessionId = await acp.CreateSessionAsync(baseUrl, options.Model, cts.Token);
            freshSession = true;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[bridge] ERROR: session setup failed: {ex.Message}");
        return 1;
    }

    // Host sessions act, so they start in build mode — new ACP sessions
    // default to plan (read-only), which cannot run a server. --plan opts
    // back into read-only. Explicit either way, so attach + fresh agree.
    var buildMode = false;
    try
    {
        var mode = await acp.SetModeAsync(baseUrl, sessionId, options.Plan ? "plan" : "build", cts.Token);
        buildMode = mode.Equals("build", StringComparison.OrdinalIgnoreCase);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[bridge] WARNING: could not set session mode ({ex.Message}); continuing read-only.");
    }

    // Standing orders for fresh sessions: container tools see only the
    // workspace — all host work goes through HOST_EXEC as the run-as user.
    // Attached sessions already have them; never repeat mid-conversation.
    var preamble = freshSession
        ? OperatorPreamble.Build(workDir, identity.RunAsUser, HostLogDirFor(identity.RunAsUser), identity.AllowSudo)
        : "";

    Console.Error.WriteLine($"[bridge] session {sessionId} on {baseUrl} (workdir {workDir})");
    var endpointHint = AgentContainer.ReadSettings().Endpoint;
    if (string.IsNullOrWhiteSpace(endpointHint))
        endpointHint = "llm endpoint (see ~/.openmono/settings.json)";
    var statusLine = $"host session · {workDir} · run as {identity.RunAsUser} · {(buildMode ? "BUILD" : "PLAN")} · {endpointHint}";
    if (options.Verbose)
    {
        Console.Error.WriteLine(
            $"[bridge] policy: tools default={config.ToolDefault}, host-exec default={config.HostExec.Default}, " +
            $"timeout={config.TimeoutMs}ms, logs={config.ResolvedLogDir}, " +
            $"run_as={identity.RunAsUser}, allow_sudo={identity.AllowSudo.ToString().ToLowerInvariant()}, " +
            $"ui={(useTui ? "tui" : "line")}");
    }

    IBridgeUi ui;
    TuiFrontend? tui = null;
    if (useTui)
    {
        tui = new TuiFrontend(options.Model ?? "");
        tui.BuildMode = buildMode;
        ui = new TuiUi(tui);
    }
    else
    {
        ui = new LineUi(options.NonInteractive, Console.Out, Console.Error);
    }
    var bridge = new Bridge(acp, config, executor, identity, ui, workDir, options.NonInteractive, buildMode);

    var task = options.TaskText;
    if (task is null && options.TaskFile is not null)
        task = await File.ReadAllTextAsync(options.TaskFile, cts.Token);

    if (task is not null)
    {
        var message = preamble.Length > 0 ? preamble + task : task;
        if (tui is not null)
        {
            tui.Enter(options.Model ?? "", endpointHint, statusLine);
            try
            {
                tui.BeginTurn();
                try
                {
                    tui.AddUserMessage(task);
                    return await bridge.RunTurnAsync(baseUrl, sessionId, new { message }, cts.Token);
                }
                finally
                {
                    tui.EndTurn();
                }
            }
            finally
            {
                tui.Exit();
            }
        }
        return await bridge.RunTurnAsync(baseUrl, sessionId, new { message }, cts.Token);
    }

    if (tui is not null)
        return await RunTuiAsync(tui, bridge, baseUrl, sessionId, statusLine, options.Model ?? "", endpointHint, preamble, cts.Token);
    return RunRepl(bridge, baseUrl, sessionId, preamble, cts.Token);
}
finally
{
    if (container is not null && !options.KeepContainer)
        await container.StopIfSpawnedAsync(weSpawned, CancellationToken.None);
}

static async Task<(string BaseUrl, AgentContainer? Container, bool WeSpawned)> ResolveAgentAsync(
    BridgeOptions options, BridgeConfig config, string workDir, CancellationToken ct)
{
    if (!string.IsNullOrWhiteSpace(options.AcpUrl))
        return (options.AcpUrl.TrimEnd('/'), null, false);
    if (!string.IsNullOrWhiteSpace(config.Acp))
        return (config.Acp.TrimEnd('/'), null, false);
    if (options.NoSpawn)
        return (ReadLockfileBase(workDir), null, false);

    var composeFile = ResolveComposeFile(workDir);
    var hostPort = options.Port ?? ParsePort(config.Acp) ?? 7475;
    var container = new AgentContainer(composeFile, workDir, Console.Error);
    var wasRunning = container.WasRunning();
    var baseUrl = await container.EnsureAsync(hostPort, options.Fresh, ct);
    await WaitDiscoveryAsync(baseUrl, ct);
    return (baseUrl, container, !wasRunning || options.Fresh);
}

static string ResolveComposeFile(string workDir)
{
    var fromEnv = Environment.GetEnvironmentVariable("OPENMONO_REPO_DIR");
    if (!string.IsNullOrWhiteSpace(fromEnv))
    {
        var direct = Path.Combine(fromEnv, "docker", "docker-compose.yml");
        if (File.Exists(direct))
            return direct;
    }
    var dir = new DirectoryInfo(workDir);
    for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, "docker", "docker-compose.yml");
        if (File.Exists(candidate))
            return candidate;
    }
    throw new InvalidOperationException(
        "docker/docker-compose.yml not found. Run from the OpenMono.ai checkout, " +
        "or set OPENMONO_REPO_DIR (openmono agent --host sets it automatically).");
}

static string ReadLockfileBase(string workDir)
{
    var lockPath = Path.Combine(workDir, ".openmono", "agent.lock");
    if (File.Exists(lockPath))
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(lockPath));
            if (doc.RootElement.TryGetProperty("port", out var portEl) &&
                portEl.TryGetInt32(out var port))
                return $"http://127.0.0.1:{port}";
        }
        catch (Exception)
        {
        }
    }
    return "http://127.0.0.1:7475";
}

static int? ParsePort(string? url)
{
    if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        return null;
    return uri.IsDefaultPort ? null : (int?)uri.Port;
}

static async Task WaitDiscoveryAsync(string baseUrl, CancellationToken ct)
{
    using var probe = new AcpClient();
    var deadline = DateTime.UtcNow.AddMinutes(3);
    Exception? last = null;
    while (DateTime.UtcNow < deadline)
    {
        try
        {
            var info = await probe.GetDiscoveryAsync(baseUrl, ct);
            Console.Error.WriteLine(
                $"[bridge] agent ready (workspace {info.GetProperty("host_workspace").GetString()}).");
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            last = ex;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
    throw new InvalidOperationException($"ACP agent not ready at {baseUrl}: {last?.Message}");
}

static int RunRepl(Bridge bridge, string baseUrl, string sessionId, string preamble, CancellationToken ct)
{
    Console.Error.WriteLine("[bridge] interactive — type a task, /quit to exit.");
    var first = true;
    while (true)
    {
        Console.Error.Write("operator> ");
        var line = Console.ReadLine();
        if (line is null || line.Trim() is "/quit" or "/exit")
            return 0;
        if (string.IsNullOrWhiteSpace(line))
            continue;
        if (TuiInputFilter.IsLauncherEcho(line))
            continue; // stale shell line (e.g. our own launch) — never a task.
        var message = first && preamble.Length > 0 ? preamble + line : line;
        first = false;
        var code = bridge.RunTurnAsync(baseUrl, sessionId, new { message }, ct)
            .GetAwaiter().GetResult();
        if (code == 1)
            Console.Error.WriteLine("[bridge] turn failed — adjust and retry, or /quit.");
    }
}

static async Task<int> RunTuiAsync(
    TuiFrontend tui,
    Bridge bridge,
    string baseUrl,
    string sessionId,
    string statusLine,
    string model,
    string endpoint,
    string preamble,
    CancellationToken ct)
{
    tui.Enter(model, endpoint, statusLine);
    try
    {
        var first = true;
        while (true)
        {
            string line;
            try
            {
                line = tui.ReadInput();
            }
            catch (Exception)
            {
                return 0;
            }
            var trimmed = line.Trim();
            // Local commands: the server already handles /mode /build /plan
            // /compact /think /playbook — only these never leave the box.
            if (trimmed is "/quit" or "/exit")
                return 0;
            if (trimmed is "/clear")
            {
                tui.ClearConversation();
                continue;
            }
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (TuiInputFilter.IsLauncherEcho(line))
                continue; // stale shell line (e.g. our own launch) — never a task.
            // Join anything that queued mid-turn (a pasted paragraph whose lines
            // arrived separately) so it stays one turn instead of scattering.
            string? queued;
            while ((queued = tui.DequeueQueued()) is not null)
                line += "\n" + queued;
            var message = first && preamble.Length > 0 ? preamble + line : line;
            first = false;
            tui.AddUserMessage(line); // transcript shows what he typed, never the preamble.
            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            tui.CurrentTurnCts = turnCts;
            tui.BeginTurn();
            int code;
            try
            {
                code = await bridge.RunTurnAsync(baseUrl, sessionId, new { message }, turnCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Esc / Ctrl+C: the dropped SSE stream aborts the server turn.
                code = 2;
                tui.Info("[bridge] turn stopped.");
            }
            finally
            {
                tui.CurrentTurnCts = null;
                tui.EndTurn();
            }
            if (code == 1)
                tui.Info("[bridge] turn failed — adjust and retry, or /quit.");
        }
    }
    finally
    {
        tui.Exit();
    }
}

/// <summary>
/// Where the run-as user would look for agent logs. Best effort when the
/// bridge runs as someone else (Linux home convention); exact when it runs
/// as the run-as user, which is the supported layout.
/// </summary>
static string HostLogDirFor(string runAsUser)
{
    if (runAsUser.Equals(HostIdentity.CurrentUser, StringComparison.Ordinal))
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".openmono", "logs");
    return $"/home/{runAsUser}/.openmono/logs";
}
