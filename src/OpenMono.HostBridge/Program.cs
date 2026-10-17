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

// ── Sudo policy (requirement 2): never granted silently. Explicit per-run
// flags (--allow-sudo/--no-sudo) win over config. Config default is "ask":
// every elevation confirms with y/N at run time, and a yes is in-memory only
// for that one command — it is NEVER persisted. Persisted values only come
// from an explicit --init answer or `openmono config host.sudo_policy`.
var sudoPolicy = options.AllowSudoOverride switch
{
    true => SudoPolicy.Allow,
    false => SudoPolicy.Deny,
    _ => config.SudoPolicy,
};
var sudoExplicit = options.AllowSudoOverride.HasValue;
if (options.Init)
{
    if (interactive)
    {
        if (options.RunAs is null &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENMONO_HOST_RUN_AS")))
        {
            var shown = string.IsNullOrWhiteSpace(runAs) ? HostIdentity.CurrentUser : runAs;
            Console.Error.Write($"Run host commands as user [default: {shown}] (Enter=keep, type a username to change): ");
            var typed = Console.ReadLine();
            if (!string.IsNullOrWhiteSpace(typed))
                runAs = typed.Trim();
        }
        if (!string.IsNullOrWhiteSpace(runAs) && !HostIdentity.IsValidUsername(runAs))
        {
            Console.Error.WriteLine($"[bridge] ERROR: invalid username '{runAs}' (expected ^[A-Za-z_][A-Za-z0-9_-]*[$]?$).");
            return 1;
        }
        if (!sudoExplicit)
        {
            var current = config.AllowSudo ?? "ask";
            Console.Error.Write(
                $"Sudo policy for host commands that need root:\n" +
                $"  ask   = confirm each escalation (y/N per command)\n" +
                $"  allow = run without asking\n" +
                $"  deny  = never escalate\n" +
                $"Choose (a)sk / (l)allow / (d)eny [default: {current}]: ");
            var answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            var mapped = answer switch
            {
                "" => current,
                "ask" or "a" => "ask",
                "allow" or "l" => "allow",
                "deny" or "d" => "deny",
                "y" or "yes" => "allow",   // legacy y/N: yes meant "allow sudo"
                "n" or "no" => "deny",
                _ => null,
            };
            if (mapped is null)
            {
                Console.Error.WriteLine($"[bridge] ERROR: '{answer}' is not a sudo policy (use ask, allow, or deny).");
                return 1;
            }
            config.AllowSudo = mapped;
        }
        else
        {
            config.AllowSudo = options.AllowSudoOverride == true ? "allow" : "deny";
        }
        config.RunAs = runAs;
        BridgeConfig.Save(options.ConfigPath, config);
        Console.Error.WriteLine(
            $"[bridge] saved identity: run_as={(string.IsNullOrWhiteSpace(runAs) ? HostIdentity.CurrentUser : runAs)}, " +
            $"allow_sudo={config.AllowSudo}");
        return 0;
    }
    else
    {
        // Fail closed when nothing can be asked: persist only an explicit
        // choice (flag or env preset), never an assumed one.
        config.RunAs = runAs;
        if (sudoExplicit)
            config.AllowSudo = options.AllowSudoOverride == true ? "allow" : "deny";
        else
            Console.Error.WriteLine(
                "[bridge] --init non-interactive: sudo policy left as " +
                $"'{config.AllowSudo ?? "ask"}' — each escalation is confirmed [y/N] at run time. " +
                "Change later with: openmono config host.sudo_policy <ask|allow|deny>");
        BridgeConfig.Save(options.ConfigPath, config);
        return 0;
    }
}

using HostIdentity identity = new(runAs, sudoPolicy, password);
password = null; // owned by identity now (zeroed on dispose)

if (!string.IsNullOrWhiteSpace(runAs) && !HostIdentity.IsValidUsername(runAs))
{
    Console.Error.WriteLine(
        $"[bridge] ERROR: invalid run_as username '{runAs}' (expected ^[A-Za-z_][A-Za-z0-9_-]*[$]?$).");
    return 1;
}

// Fail fast on the wrong-user trap: HOST_EXEC as another user needs sudo,
// and with sudo denied every host command would fail one by one mid-session.
// Ask does not fail fast: it confirms per escalation at run time.
if (!identity.RunAsUser.Equals(HostIdentity.CurrentUser, StringComparison.Ordinal) &&
    identity.Sudo == SudoPolicy.Deny)
{
    Console.Error.WriteLine(
        $"[bridge] ERROR: run_as='{identity.RunAsUser}' but you are '{HostIdentity.CurrentUser}' and sudo is denied (allow_sudo=deny). " +
        "Host commands would fail. Run the bridge as that user, change run_as (openmono config host.run_as), or set sudo to ask/allow (openmono config host.sudo_policy).");
    return 1;
}

// ── Daemon mode (--daemon): the bare-metal half of host mode for SANDBOX
// sessions (openmono agent TUI / ACP extension). Sandbox containers pair in
// over host.docker.internal and run user-approved commands here. The operator
// path (--acp / spawn) below is unchanged. Defense in depth: pairing needs the
// shared pair_secret, every exec needs a live session token, the deny-list and
// sudo policy are enforced here as the last line of defense, and every call is
// audited. The daemon never re-consents — the client UI is where the human
// says yes to each command. See docs/DEPLOYMENT-MODES.md.
if (options.Daemon)
{
    // Pairing secret: env wins; otherwise reuse the saved config secret; a
    // fresh daemon generates one and persists it (0600) so the launcher can
    // hand it to the sandbox container. Without a secret, pairing is disabled
    // (the daemon refuses everything) — never pair with an empty secret.
    var envSecret = Environment.GetEnvironmentVariable("OPENMONO_HOST_PAIR_SECRET");
    if (!string.IsNullOrWhiteSpace(envSecret))
        config.PairSecret = envSecret.Trim();
    if (string.IsNullOrWhiteSpace(config.PairSecret))
    {
        var bytes = new byte[24];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        config.PairSecret = Convert.ToHexString(bytes);
        BridgeConfig.Save(options.ConfigPath, config);
        Console.Error.WriteLine($"[bridge] generated pair_secret in {BridgeConfig.DefaultPath} (0600).");
    }

    // Escalation at the daemon has no human to answer an "ask", so warn early
    // when sudo is not an explicit allow (the daemon still fails those closed).
    if (identity.Sudo != SudoPolicy.Allow)
        Console.Error.WriteLine(
            "[bridge] host mode: sudo policy is not 'allow' — as_root / sudo commands will be refused " +
            "(set openmono config host.sudo_policy allow to enable them for unattended host mode).");

    using var daemonCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; try { daemonCts.Cancel(); } catch (ObjectDisposedException) { } };
    // ProcessExit can fire during shutdown after the CTS is already disposed
    // (e.g. the port-busy early return) — an unguarded Cancel() crashed the
    // daemon with an unhandled ObjectDisposedException in the past.
    AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { daemonCts.Cancel(); } catch (ObjectDisposedException) { } };

    var daemonBinds = options.DaemonBinds.Count > 0
        ? options.DaemonBinds.ToArray()
        : HostDaemon.DetectDockerBridgeAddresses();

    await using var daemon = new HostDaemon(config, identity, workDir, options.DaemonPort,
        TimeSpan.FromHours(options.DaemonIdleTimeoutHours), daemonBinds);

    // Console approver: when this daemon has a real terminal, an approval-less
    // pair request prompts [y/N] right here. Without a TTY (launched by
    // openmono host daemon via nohup) the operator approves with
    // `openmono host approve <session>` instead — never a silent pair. Without
    // a console the queue is invisible, so say so up front.
    if (!Console.IsInputRedirected)
    {
        daemon.PairRequested += req => _ = Task.Run(() =>
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"[bridge] PAIR REQUEST — session {req.SessionId} · workdir {req.WorkDir}");
            Console.Error.Write("[bridge] allow this sandbox session on the host? [y/N] ");
            var answer = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            var ok = answer is "y" or "yes" or "approve" or "allow";
            if (!daemon.ResolvePairRequest(req.SessionId, ok))
                Console.Error.WriteLine("[bridge] (request already expired — too slow)");
            else
                Console.Error.WriteLine(ok ? "[bridge] pair approved." : "[bridge] pair denied.");
        });
    }
    else
    {
        daemon.PairRequested += req =>
            Console.Error.WriteLine(
                $"[bridge] PAIR REQUEST queued — session {req.SessionId} · workdir {req.WorkDir} · " +
                $"no console attached (nohup daemon): approve with `openmono host approve {req.SessionId}`");
    }
    try
    {
        daemon.Start();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[bridge] ERROR: could not listen on 127.0.0.1:{options.DaemonPort}: {ex.Message}");
        return 1;
    }

    Console.Error.WriteLine(
        $"[bridge] host daemon on {string.Join(" + ", daemon.BoundAddresses.Select(a => $"http://{a}:{options.DaemonPort}"))} · workdir {workDir} · " +
        $"run as {identity.RunAsUser} · sudo={identity.Sudo.ToString().ToLowerInvariant()} · " +
        "sandbox sessions pair with: openmono agent → /host on");
    if (Console.IsInputRedirected)
        Console.Error.WriteLine("[bridge] no console attached — pair requests queue; approve with: openmono host requests / openmono host approve <id>");
    Console.Error.WriteLine("[bridge] Ctrl+C to stop. Every host command is audited + policy-gated.");

    await Task.Delay(Timeout.Infinite, daemonCts.Token).ContinueWith(_ => { });
    Console.Error.WriteLine("[bridge] host daemon stopped (all session tokens revoked).");
    return 0;
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
        ? OperatorPreamble.Build(workDir, identity.RunAsUser, HostLogDirFor(identity.RunAsUser), identity.Sudo)
        : "";

    Console.Error.WriteLine($"[bridge] session {sessionId} on {baseUrl} (workdir {workDir})");
    var endpointHint = AgentContainer.ReadSettings().Endpoint;
    if (string.IsNullOrWhiteSpace(endpointHint))
        endpointHint = "llm endpoint (see ~/.openmono/settings.json)";
    var sudoMode = identity.Sudo.ToString().ToLowerInvariant();
    var statusLine = $"host session · {workDir} · run as {identity.RunAsUser} · sudo={sudoMode} · {(buildMode ? "BUILD" : "PLAN")} · HOST:ON · {endpointHint}";
    if (options.Verbose)
    {
        Console.Error.WriteLine(
            $"[bridge] policy: tools default={config.ToolDefault}, host-exec default={config.HostExec.Default}, " +
            $"timeout={config.TimeoutMs}ms, logs={config.ResolvedLogDir}, " +
            $"run_as={identity.RunAsUser}, allow_sudo={identity.Sudo.ToString().ToLowerInvariant()}, " +
            $"ui={(useTui ? "tui" : "line")}");
    }

    IBridgeUi ui;
    TuiFrontend? tui = null;
    if (useTui)
    {
        tui = new TuiFrontend(options.Model ?? "", workDir);
        tui.BuildMode = buildMode;
        ui = new TuiUi(tui);
    }
    else
    {
        ui = new LineUi(options.NonInteractive, Console.Out, Console.Error);
    }
    var bridge = new Bridge(acp, config, executor, identity, ui, workDir, options.NonInteractive, buildMode);

    // Line mode: surface the effective sudo policy without --verbose so an
    // operator can confirm the mode at a glance (TUI shows it in the status line).
    if (!useTui)
    {
        ui.Info(
            $"[bridge] policy: run_as={identity.RunAsUser}, " +
            $"host-exec default={config.HostExec.Default}, allow_sudo={sudoMode}");
    }

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
        if (line.Trim() is "/host" or "/host status")
        {
            Console.Error.WriteLine("[bridge] HOST:ON — whole-machine session (HOST_EXEC audited + policy-gated).");
            Console.Error.WriteLine("           Sandboxed instead: exit and run: openmono agent");
            continue;
        }
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
            if (trimmed is "/host" or "/host status" or "/host on")
            {
                tui.Info("HOST:ON — whole-machine session (HOST_EXEC audited + policy-gated). Sandboxed instead: /quit, then openmono agent.");
                continue;
            }
            if (trimmed is "/host off")
            {
                tui.Info("To return to sandboxed mode: /quit, then run: openmono agent");
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
