using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenMono.Acp;
using OpenMono.Commands;
using OpenMono.Config;
using OpenMono.History;
using OpenMono.Hooks;
using OpenMono.Llm;
using OpenMono.Lsp;
using OpenMono.Mcp;
using OpenMono.Memory;
using OpenMono.Permissions;
using OpenMono.Playbooks;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;
using OpenMono.Utils;

string? endpoint = null, model = null, workdir = null, configPath = null;
var verbose = false;
var showDetail = false;
bool? useTui = null;
var noAcp = false;
int? acpPort = null;
var acpOnly = false;

if (EnvFlag_Truthy(Environment.GetEnvironmentVariable("OPENMONO_ACP_ONLY")))
    acpOnly = true;

for (var i = 0; i < args.Length; i++)
{
    var arg = args[i];
    var next = i + 1 < args.Length ? args[i + 1] : null;

    switch (arg)
    {
        case "--endpoint" when next is not null: endpoint = next; i++; break;
        case "--model" when next is not null: model = next; i++; break;
        case "--workdir" when next is not null: workdir = next; i++; break;
        case "--config" when next is not null: configPath = next; i++; break;
        case "--verbose" or "-v": verbose = true; break;
        case "--detail": showDetail = true; break;
        case "--tui": useTui = true; break;
        case "--classic": useTui = false; break;
        case "--no-acp": noAcp = true; break;
        case "--acp-port" when next is not null && int.TryParse(next, out var p): acpPort = p; i++; break;
        case "--acp-only":
            {
                var (val, consumed) = AcpOnlyArg.Parse(next);
                acpOnly = val;
                if (consumed) i++;
                break;
            }
        case "--help" or "-h":
            Console.WriteLine("OpenMono.ai — Local Coding Agent");
            Console.WriteLine();
            Console.WriteLine("Usage: openmono [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --endpoint <url>   LLM server endpoint (default: http://localhost:7474)");
            Console.WriteLine("  --model <name>     Model name (default: auto-detected from server via /props)");
            Console.WriteLine("  --workdir <path>   Working directory (default: current directory)");
            Console.WriteLine("  --config <path>    Path to settings.json override");
            Console.WriteLine("  --verbose, -v      Show LLM request/response debug info");
            Console.WriteLine("  --detail           Show the right-hand detail panel in the TUI");
            Console.WriteLine("  --tui              Force full-screen TUI mode (default for interactive)");
            Console.WriteLine("  --classic          Force classic scrolling terminal mode");
            Console.WriteLine("  --no-acp           Force-disable the ACP agent server (overrides config)");
            Console.WriteLine("  --acp-port <n>     ACP server port (default: 7475 or acpServer.Port in config)");
            Console.WriteLine("  --acp-only [bool]  Run the ACP server only — no TUI. Bare or `--acp-only true` forces");
            Console.WriteLine("                     it on (container default); `--acp-only false` runs the interactive TUI.");
            Console.WriteLine("                     Without the flag, ACP stays off unless acpServer.enabled = true in config.");
            Console.WriteLine("  --help, -h         Show this help message");
            Console.WriteLine("  --version          Show version");
            Console.WriteLine();
            Console.WriteLine("Slash commands (type / inside the REPL, or use Ctrl+P):");
            Console.WriteLine("  /help              List all commands");
            Console.WriteLine("  /status            Current session info (turns, tokens, model)");
            Console.WriteLine("  /stats             Token usage and tool analytics");
            Console.WriteLine("  /compact           Summarize history to free context space");
            Console.WriteLine("  /clear             Wipe conversation and start fresh");
            Console.WriteLine("  /retry             Resend the last message");
            Console.WriteLine("  /undo [n]          Revert last n file modification(s)");
            Console.WriteLine("  /checkpoint        Checkpoint conversation to free context");
            Console.WriteLine("  /think             Toggle step-by-step reasoning mode");
            Console.WriteLine("  /mode              Toggle between Plan mode (read-only) and Build mode (write)");
            Console.WriteLine("  /init              Auto-generate OPENMONO.md from project");
            Console.WriteLine("  /resume [id]       Restore a previous session");
            Console.WriteLine("  /export            Export conversation (markdown/json/html)");
            Console.WriteLine("  /debug             Toggle verbose debug output");
            Console.WriteLine("  /quit              Exit OpenMono");
            Console.WriteLine();
            Console.WriteLine("Keyboard shortcuts:");
            Console.WriteLine("  Ctrl+C             Clear input / clear context (empty input) / cancel turn");
            Console.WriteLine("  Ctrl+C Ctrl+C      Exit");
            Console.WriteLine("  Ctrl+U             Kill current input line");
            Console.WriteLine("  Ctrl+W             Delete last word");
            Console.WriteLine("  Up / Down          Navigate input history");
            Console.WriteLine("  Tab                Autocomplete slash command");
            Console.WriteLine("  PgUp / PgDn        Scroll conversation");
            return 0;
        case "--version":
            Console.WriteLine("OpenMono.ai v1.7.0");
            return 0;
    }
}

await RunAgentAsync(endpoint, model, workdir, configPath, verbose, showDetail, useTui, noAcp, acpPort, acpOnly);
return 0;

static bool EnvFlag_Truthy(string? v) =>
    v is not null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));

static async Task RunAgentAsync(string? endpoint, string? model, string? workdir, string? configPath, bool verbose = false, bool showDetail = false, bool? useTui = null, bool noAcp = false, int? acpPort = null, bool acpOnly = false)
{

    IRenderer renderer = new TerminalRenderer();
    var config = ConfigLoader.Load(workdir, configPath, warn: msg => renderer.WriteWarning(msg));
    if (endpoint is not null) config.Llm.Endpoint = endpoint;

    await TryDetectActualModelAsync(config);
    if (model is not null) config.Llm.Model = model;
    if (verbose) config.Verbose = true;
    if (showDetail) config.ShowDetail = true;
    renderer.Verbose = config.Verbose;

    Log.Initialize(config.DataDirectory);
    Log.Info($"Session starting — model={config.Llm.Model} endpoint={config.Llm.Endpoint} workdir={config.WorkingDirectory}");

    var sessionManager = new SessionManager(config);
    var session = SessionManager.CreateSession();



    var enableTui = !acpOnly && (useTui ?? (!Console.IsInputRedirected && !Console.IsOutputRedirected));
    AnsiTuiRenderer? ansiTui = null;
    AppDomain.CurrentDomain.UnhandledException += (_, _) => ansiTui?.SafeExit();
    if (enableTui)
    {
        ansiTui = new AnsiTuiRenderer(config, session, new ConsoleTerminal());
        ansiTui.Verbose = config.Verbose;
        renderer = ansiTui;
    }

    var permissions = new PermissionEngine(config, renderer, renderer);
    var memoryStore = new MemoryStore(config.DataDirectory);
    var hookRunner = new HookRunner(config, warn: msg => renderer.WriteWarning(msg));

    var providerRegistry = new ProviderRegistry();
    using var llm = providerRegistry.CreateClient(config);

    Action<string> debugCallback = msg => renderer.WriteDebug(msg);
    if (llm is OpenAiCompatClient openAiClient)
    {
        openAiClient.OnDebug = debugCallback;
        openAiClient.OnModelReported = m => config.Llm.Model = m;
    }
    if (llm is AnthropicClient anthropicClient) anthropicClient.OnDebug = debugCallback;

    var fileHistory = new FileHistory(config);
    session.Meta.FileHistory = fileHistory;

    using var lspManager = new LspServerManager(config.WorkingDirectory, msg => renderer.WriteInfo(msg));

    var tokenTracker = new TokenTracker();
    session.Meta.TokenTracker = tokenTracker;

    if (ansiTui is not null)
    {
        tokenTracker.OnUsageUpdated = (_, _) => ansiTui.OnTokensUpdated();
    }
    var tools = new ToolRegistry();
    tools.Register(new FileReadTool());
    tools.Register(new FileWriteTool());
    tools.Register(new FileEditTool());
    tools.Register(new GlobTool());
    tools.Register(new GrepTool());
    tools.Register(new BashTool());
    tools.Register(new AgentTool());
    tools.Register(new TodoTool());
    tools.Register(new AskUserTool());
    tools.Register(new MemorySaveTool(memoryStore));
    tools.Register(new WebFetchTool());
    tools.Register(new WebSearchTool());
    tools.Register(new ListDirectoryTool());
    tools.Register(new ApplyPatchTool());
    tools.Register(new EnterPlanModeTool());
    tools.Register(new CreatePlanTool());
    tools.Register(new ImplementPlanTool());
    tools.Register(new LspTool(lspManager));

    var refDir = ResolveRefDirectory(config);
    tools.Register(new RoslynTool(referenceDirectory: refDir));

    if (config.AutoDetectCodeGraph)
    {
        await AutoDetectCodeGraphAsync(config, renderer);
    }

    var graphifyJson = Path.Combine(config.WorkingDirectory, "graphify-out", "graph.json");
    if (File.Exists(graphifyJson))
        renderer.WriteInfo("graphify graph detected — use: graphify query/path/explain via Bash.");
    else if (File.Exists(Path.Combine(config.WorkingDirectory, "graphify-out", "GRAPH_REPORT.md")))
        renderer.WriteInfo("graphify-out/ found but graph.json missing. Run: openmono graphify");

    var playbookLoader = new PlaybookLoader(config.Playbooks.Paths);
    var playbookRegistry = new PlaybookRegistry();
    playbookRegistry.RegisterAll(playbookLoader.LoadAll());
    var playbookExecutor = new PlaybookExecutor(llm, tools, renderer, config, permissions);
    tools.Register(new PlaybookTool(playbookRegistry, playbookExecutor));

    var systemPrompt = await SystemPrompt.BuildAsync(config, memoryStore, playbookRegistry);
    session.AddMessage(new Message { Role = MessageRole.System, Content = systemPrompt });

    using var mcpManager = new McpServerManager(msg => renderer.WriteInfo(msg));
    var mcpConfigs = config.McpServers.Select(kv => new McpServerConfig
    {
        Name = kv.Key,
        Command = kv.Value.Command,
        Args = kv.Value.Args,
        Env = kv.Value.Env,
        Enabled = kv.Value.Enabled,
    });
    await mcpManager.InitializeAsync(mcpConfigs, tools, CancellationToken.None);

    var acp = config.AcpServer ?? new AcpServerSettings();
    AcpHostedService? acpHost = null;
    CancellationTokenSource? acpCts = null;
    Exception? acpStartError = null;

    if (acpOnly && noAcp)
    {
        renderer.WriteError("--acp-only and --no-acp are mutually exclusive.");
        return;
    }





    if (acpOnly) acp.Enabled = true;
    if (EnvFlag_Truthy(Environment.GetEnvironmentVariable("OPENMONO_ACP_ENABLED")))
        acp.Enabled = true;

    if (acp.Enabled && !noAcp)
    {
        if (acpPort.HasValue) acp.Port = acpPort.Value;
        if (Environment.GetEnvironmentVariable("ACP_PORT") is { Length: > 0 } envPort
            && int.TryParse(envPort, out var envPortInt))
            acp.Port = envPortInt;




        var hostWorkspaceExternal = Environment.GetEnvironmentVariable("HOST_WORKSPACE_PATH");




        var runningInDocker = File.Exists("/.dockerenv");
        acp.BindAllInterfaces = runningInDocker;

        Environment.SetEnvironmentVariable("HOST_WORKSPACE_PATH",
            hostWorkspaceExternal ?? config.WorkingDirectory);
        Environment.SetEnvironmentVariable("HOST_ACP_PORT",
            Environment.GetEnvironmentVariable("HOST_ACP_PORT") ?? acp.Port.ToString());







        var lockWorkspaceMount = runningInDocker ? "/workspace" : config.WorkingDirectory;

        var acpServices = new ServiceCollection();
        acpServices.AddSingleton(config);
        acpServices.AddSingleton<ILlmClient>(llm);
        acpServices.AddSingleton(tools);
        acpServices.AddSingleton<IOutputSink>(renderer);
        acpServices.AddSingleton<IInputReader>(renderer);
        acpServices.AddSingleton<ILiveFeedback>(renderer);

        acpServices.AddSingleton(new AcpSessionStore(config, acp));
        var lockFileWriter = new AcpLockFileWriter(acp, lockWorkspaceMount);
        acpServices.AddSingleton(lockFileWriter);
        acpServices.AddSingleton(playbookRegistry);
        acpServices.AddSingleton(memoryStore);
        acpServices.AddSingleton(sp => new ConversationLoopFactory(
            sp.GetRequiredService<ILlmClient>(),
            sp.GetRequiredService<ToolRegistry>(),
            sp.GetRequiredService<AppConfig>(),
            sp.GetRequiredService<IOutputSink>(),
            sp.GetRequiredService<IInputReader>(),
            sp.GetRequiredService<ILiveFeedback>(),
            sp.GetRequiredService<PlaybookRegistry>(),
            sp.GetRequiredService<MemoryStore>()));
        acpServices.AddSingleton(sp => new AcpTurnRunnerFactory(
            sp.GetRequiredService<ConversationLoopFactory>(),
            sp.GetRequiredService<AcpServerSettings>(),
            sp.GetRequiredService<PlaybookRegistry>(),
            sp.GetRequiredService<MemoryStore>()));

        acpCts = new CancellationTokenSource();
        acpHost = new AcpHostedService(acp, acpServices, lockFileWriter);
        try
        {
            await acpHost.StartAsync(acpCts.Token);
            renderer.WriteInfo($"ACP server listening on http://127.0.0.1:{acp.Port}");
            renderer.WriteInfo($"Lock file: {lockFileWriter.LockFilePath}");
        }
        catch (Exception ex)
        {




            acpStartError = ex;
            renderer.WriteWarning($"ACP server failed to start: {ex.Message}");
            if (!acpOnly)
                renderer.WriteWarning("Continuing without ACP. Pass --no-acp to silence this on subsequent runs.");
            await acpHost.DisposeAsync();
            acpHost = null;
            acpCts.Dispose();
            acpCts = null;
        }
    }

    if (acpOnly)
    {
        if (acpHost is null)
        {
            renderer.WriteError(AcpStartupError.Message(acp.Port, acpStartError));
            return;
        }

        renderer.WriteInfo("Running in --acp-only mode. Send SIGINT (Ctrl+C) to stop.");

        var shutdown = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.TrySetResult();
        };

        await shutdown.Task;
        renderer.WriteInfo("Stopping ACP server…");
        await acpHost.StopAsync(CancellationToken.None);
        await acpHost.DisposeAsync();
        return;
    }

    var checkpointer = new Checkpointer(llm, config.Llm.ContextSize);

    var commands = new CommandRegistry();
    commands.Register(new HelpCommand());
    commands.Register(new StatusCommand());
    commands.Register(new StatsCommand());
    commands.Register(new InitCommand());
    commands.Register(new UndoCommand());
    commands.Register(new DebugCommand());
    commands.Register(new ResumeCommand());
    commands.Register(new ExportCommand());
    commands.Register(new ClearCommand());
    commands.Register(new CheckpointCommand(checkpointer));
    commands.Register(new ThinkCommand());
    commands.Register(new ModeCommand());

    var compactor = new Compactor(llm, config.Llm.ContextSize);
    var loop = new ConversationLoop(llm, tools, permissions, renderer, renderer, renderer, config, session, compactor, memoryStore,
        checkpointer: checkpointer);

    commands.Register(new RetryCommand(loop));
    commands.Register(new CompactCommand(compactor));

    renderer.EnableCommandSuggestions(commands);

    await hookRunner.RunSessionStartHooksAsync(CancellationToken.None);

    ansiTui?.EnterFullScreen();

    renderer.WriteWelcome(config.Llm.Model, config.Llm.Endpoint);

    Task? warmupTask = null;
    if (!await IsServerWarmAsync(config.Llm.Endpoint))
    {
        renderer.WriteInfo("Warming KV cache in background — first response will be slower.");
        warmupTask = SendWarmupAsync(config.Llm.Endpoint, systemPrompt, tools.BuildToolDefinitions(), config.Llm.Model);
    }

    var lastCtrlCExitTime = DateTime.MinValue;

    CancellationTokenSource? currentTurnCts = null;

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        var now = DateTime.UtcNow;

        if (currentTurnCts is { IsCancellationRequested: false })
        {
            currentTurnCts.Cancel();
            lastCtrlCExitTime = now;

            if (ansiTui is null)
                Console.Error.WriteLine("\x1b[43;30m  ^C  Press Ctrl+C one more time to exit  \x1b[0m");
            return;
        }

        if ((now - lastCtrlCExitTime).TotalSeconds <= 1.5)
        {




            try { acpHost?.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception ex) { Log.Debug($"ACP host stop on exit failed: {ex.Message}"); }
            ProcessWatchdog.ScheduleHardKill();
            ansiTui?.SafeExit();
            Environment.Exit(0);
        }
        lastCtrlCExitTime = now;
    };

    while (true)
    {
        string input;
        try
        {
            input = InputSanitizer.SanitizeUserInput(renderer.ReadInput());
        }
        catch (OperationCanceledException)
        {

            break;
        }

        if (string.IsNullOrWhiteSpace(input))
            continue;

        if (input.Trim() is "exit" or "quit" or "q")
        {
            if (currentTurnCts is { } activeCts && !activeCts.IsCancellationRequested)
                activeCts.Cancel();
            else
                renderer.WriteInfo("Nothing is running. Type /quit to exit OpenMono.");
            continue;
        }

        if (input.StartsWith('/'))
        {

            if (input.Trim() == "/")
            {
                try
                {
                    var picked = renderer.ShowCommandPicker(commands);
                    if (picked is null) continue;
                    input = picked;
                }
                catch
                {
                    input = "/help";
                }
            }

            var parts = input.Split(' ', 2);
            var cmdName = parts[0];
            var cmdArgs = parts.Length > 1 ? parts[1..] : Array.Empty<string>();

            if (cmdName is "/quit" or "/exit" or "/q")
            {
                ProcessWatchdog.ScheduleHardKill();
                ansiTui?.SafeExit();
                Environment.Exit(0);
            }

            var cmd = commands.Resolve(cmdName);
            if (cmd is not null)
            {
                using var cmdCts = new CancellationTokenSource();
                currentTurnCts = cmdCts;
                var ctx = new CommandContext
                {
                    Session = session,
                    ToolRegistry = tools,
                    CommandRegistry = commands,
                    Config = config,
                    Renderer = renderer,
                    WorkingDirectory = config.WorkingDirectory,
                };
                try
                {
                    await cmd.ExecuteAsync(cmdArgs, ctx, cmdCts.Token);
                }
                catch (OperationCanceledException)
                {
                    renderer.WriteWarning("Command cancelled.");
                    Log.Info("Command cancelled by user.");
                }
                finally
                {
                    currentTurnCts = null;
                }
                continue;
            }

            renderer.WriteWarning($"Unknown command: {cmdName}. Type / to see available commands.");
            continue;
        }

        // Transform relative @ file references to absolute paths
        // The agent will call FileRead to load files (per system prompt)
        var transformedInput = FileReferenceResolver.TransformRelativeReferences(input, config.WorkingDirectory);

        // TUI plan-decision menu: when a plan is awaiting a decision (CreatePlan presented one
        // and we're still in Plan mode), map 1/2/3 to the same routing the extension buttons use.
        if (session.Meta.PlanMode && session.Meta.LastPlanContent is { Length: > 0 })
        {
            var choice = input.Trim().ToLowerInvariant() switch
            {
                "1" or "auto" or "implement" => "auto",
                "2" or "ask" or "gated" => "gated",
                "3" or "keep" => "keep",
                _ => null,
            };
            if (choice == "keep")
            {
                renderer.WriteInfo("Staying in Plan mode — refine the plan, or pick 1 (auto) / 2 (ask before edits).");
                continue;
            }
            if (choice is "auto" or "gated")
            {
                var (_, autoApprove, instruction) = ModeInstructions.ResolvePlanDecision(choice);
                session.Meta.PlanMode = false;
                session.Meta.AutoApproveWrites = autoApprove;
                session.Meta.LastPlanContent = null;
                renderer.WriteInfo($"Switched to Build mode — implementing{(autoApprove ? "" : " (you'll be prompted before edits)")}.");
                transformedInput = instruction;
            }
            // else: unrecognized input → treat as a plan refinement (fall through normally).
        }

        ansiTui?.AddUserMessage(input);
        using var turnCts = new CancellationTokenSource();
        currentTurnCts = turnCts;
        if (ansiTui is not null) ansiTui.CurrentTurnCts = turnCts;
        if (warmupTask is not null)
        {
            renderer.WriteInfo(warmupTask.IsCompleted
                ? "KV cache warm — responses will be fast."
                : "KV cache still warming — this response will be slower.");
            warmupTask = null;
        }

        try
        {
            await loop.RunTurnAsync(transformedInput, imageParts: null, turnCts.Token);
        }
        catch (OperationCanceledException)
        {
            renderer.WriteWarning("Request cancelled.");
            Log.Info("Request cancelled by user.");
        }
        catch (HttpRequestException ex)
        {
            if (ex.StatusCode is null)
            {
                renderer.WriteError($"LLM error: {ex.Message}");
                await TryRecoverLlamaServerAsync(renderer, config.WorkingDirectory, config.Llm.Endpoint);
            }
            else
            {
                renderer.WriteError($"LLM error: {ex.Message}");
                var hint = ex.StatusCode switch
                {
                    System.Net.HttpStatusCode.InternalServerError =>
                        "llama-server returned 500. Likely causes: context too long (KV cache overflow), " +
                        "out of GPU memory, or model crash.\n" +
                        "Check logs: docker logs llama-server --tail 40",
                    System.Net.HttpStatusCode.ServiceUnavailable =>
                        "llama-server is busy or still loading — wait a moment and try again.\n" +
                        "Check status: curl http://localhost:7474/health",
                    System.Net.HttpStatusCode.TooManyRequests =>
                        "llama-server rate limit hit — wait a moment and try again.",
                    System.Net.HttpStatusCode.BadRequest =>
                        "llama-server rejected the request (400). The conversation may be malformed.\n" +
                        "Try starting a new session: /new",
                    _ =>
                        "Check: curl http://localhost:7474/health",
                };
                renderer.WriteInfo(hint);
            }
            Log.Error("LLM connection failed", ex);
        }
        catch (Exception ex)
        {
            renderer.WriteError($"Unexpected error: {ex.Message}");
            if (Log.LogPath is not null)
                renderer.WriteInfo($"Details logged to: {Log.LogPath}");
            Log.Error("Unhandled exception in conversation turn", ex);
        }
        finally
        {
            currentTurnCts = null;
            if (ansiTui is not null) ansiTui.CurrentTurnCts = null;
        }

        try
        {
            await sessionManager.SaveAsync(session, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Warn($"Session autosave failed (turn loop): {ex.Message}");
        }
    }

    ansiTui?.ExitFullScreen();

    await sessionManager.SaveAsync(session, CancellationToken.None);
    renderer.WriteInfo($"Session saved: {session.Id}");
}

static async Task<bool> IsServerWarmAsync(string endpoint)
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var metrics = await http.GetStringAsync($"{endpoint.TrimEnd('/')}/metrics");
        var line = metrics.Split('\n')
            .FirstOrDefault(l => l.StartsWith("llamacpp:prompt_tokens_total"));
        if (line is not null && double.TryParse(line.Split(' ').LastOrDefault(), out var tokens) && tokens > 0)
        {
            Log.Debug($"[warmup] Server already processed {tokens} prompt tokens — skipping.");
            return true;
        }
    }
    catch (Exception ex) { Log.Debug($"Server warmth probe failed: {ex.Message}"); }
    return false;
}

static async Task SendWarmupAsync(string endpoint, string systemPrompt, System.Text.Json.JsonElement toolDefs, string model)
{
    var baseUrl = endpoint.TrimEnd('/');
    var apiKey = Environment.GetEnvironmentVariable("OPENMONO_API_KEY")
               ?? Environment.GetEnvironmentVariable("LLAMA_API_KEY")
               ?? "";

    var bodyDict = new Dictionary<string, object?>
    {
        ["model"] = model,
        ["messages"] = new object[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user",   content = "ping" },
        },
        ["max_tokens"] = 1,
        ["temperature"] = 0.0,
        ["stream"] = false,
        ["tool_choice"] = "none",
    };

    if (toolDefs.ValueKind == System.Text.Json.JsonValueKind.Array && toolDefs.GetArrayLength() > 0)
        bodyDict["tools"] = toolDefs;

    var json = System.Text.Json.JsonSerializer.Serialize(bodyDict);
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/chat/completions")
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };
    if (!string.IsNullOrEmpty(apiKey))
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");

    var t0 = DateTime.UtcNow;
    try
    {
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var elapsed = (DateTime.UtcNow - t0).TotalSeconds;
        Log.Debug($"[warmup] Done in {elapsed:F1}s — full prompt + tools cached.");
    }
    catch (Exception ex)
    {
        Log.Debug($"[warmup] Failed (non-fatal): {ex.Message}");
    }
}

static async Task TryRecoverLlamaServerAsync(IRenderer renderer, string workingDirectory, string endpoint)
{
    renderer.WriteWarning($"llama-server isn't reachable at {endpoint}");

    var healthUrl = $"{endpoint.TrimEnd('/')}/health";
    var composeFile = Path.Combine(workingDirectory, "docker", "docker-compose.yml");
    var composeDir = Path.GetDirectoryName(composeFile)!;

    var dockerBin = OperatingSystem.IsWindows() ? "docker.exe" : "docker";
    var dockerAvailable = (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator)
        .Any(dir => File.Exists(Path.Combine(dir, dockerBin)));

    if (!dockerAvailable)
    {
        renderer.WriteWarning("docker is not accessible from inside this container.");
        renderer.WriteInfo("On your host machine run:");
        renderer.WriteInfo("  cd ~/openmono.ai/docker");
        renderer.WriteInfo("  docker compose up -d llama-server");
        var hostHealthUrl = healthUrl.Replace("llama-server", "localhost");
        renderer.WriteInfo($"Check: curl {hostHealthUrl}  (HTTP 200 = ready)");
        renderer.WriteInfo($"Watching {healthUrl} — I'll notify you when it's up (Ctrl+C to skip)...");
        await PollHealthAsync(renderer, healthUrl, timeoutSeconds: 300);
        return;
    }

    var answer = await renderer.AskUserAsync("Start llama-server now? [y/N]", CancellationToken.None);
    if (!answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) &&
        !answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase))
        return;

    renderer.WriteInfo("Starting llama-server...");
    try
    {
        var startDir = File.Exists(composeFile) ? composeDir : workingDirectory;
        var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "docker",
            Arguments = "compose --profile full up -d llama-server",
            UseShellExecute = false,
            WorkingDirectory = startDir,
        });
        if (proc is null)
        {
            renderer.WriteWarning("Could not launch docker — is Docker installed and on PATH?");
            return;
        }
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
        {
            renderer.WriteWarning("docker compose failed — run it manually to see the error output.");
            return;
        }

        renderer.WriteInfo($"Container started. Polling {healthUrl}...");
        await PollHealthAsync(renderer, healthUrl, timeoutSeconds: 120);
    }
    catch (Exception startEx)
    {
        renderer.WriteWarning($"Could not start llama-server: {startEx.Message}");
    }
}

static async Task PollHealthAsync(IRenderer renderer, string healthUrl, int timeoutSeconds)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    var started = DateTime.UtcNow;
    var deadline = started.AddSeconds(timeoutSeconds);
    var lastStatus = "";

    while (DateTime.UtcNow < deadline)
    {
        var elapsed = (int)(DateTime.UtcNow - started).TotalSeconds;
        string status;
        bool ready, isHttpResponse;
        try
        {
            var resp = await http.GetAsync(healthUrl);
            var body = (await resp.Content.ReadAsStringAsync()).Trim();
            var snippet = body.Length > 120 ? body[..120] + "…" : body;
            status = $"HTTP {(int)resp.StatusCode} — {snippet}";
            ready = resp.IsSuccessStatusCode;
            isHttpResponse = true;
        }
        catch (Exception pollEx)
        {
            status = pollEx.InnerException?.Message ?? pollEx.Message;
            ready = false;
            isHttpResponse = false;
        }

        if (status != lastStatus)
        {
            if (isHttpResponse)
                renderer.WriteInfo($"  [{elapsed,3}s] → {status}");
            else
                renderer.WriteWarning($"[{elapsed,3}s] → {status}");
            lastStatus = status;
        }

        if (ready)
        {
            renderer.WriteToolSuccess($"Ready after {elapsed}s — retry your message.");
            return;
        }

        await Task.Delay(2000);
    }

    renderer.WriteWarning($"Timed out after {timeoutSeconds}s. Last response: {lastStatus}");
    renderer.WriteInfo("Check logs: docker logs llama-server --tail 20");
}

static async Task TryDetectActualModelAsync(AppConfig config)
{
    try
    {
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var url = $"{config.Llm.Endpoint.TrimEnd('/')}/props";
        var json = await http.GetStringAsync(url);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? name = null;
        if (root.TryGetProperty("model_alias", out var alias)
            && alias.ValueKind == System.Text.Json.JsonValueKind.String
            && !string.IsNullOrWhiteSpace(alias.GetString()))
        {
            name = alias.GetString();
        }
        else if (root.TryGetProperty("model_path", out var path)
            && path.ValueKind == System.Text.Json.JsonValueKind.String
            && !string.IsNullOrWhiteSpace(path.GetString()))
        {
            name = DisplayNameFromPath(path.GetString()!);
        }
        else if (root.TryGetProperty("default_generation_settings", out var ds)
            && ds.ValueKind == System.Text.Json.JsonValueKind.Object
            && ds.TryGetProperty("model", out var m)
            && m.ValueKind == System.Text.Json.JsonValueKind.String
            && !string.IsNullOrWhiteSpace(m.GetString()))
        {
            name = DisplayNameFromPath(m.GetString()!);
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            config.Llm.Model = name!;
            Log.Debug($"Detected model from /props: {name}");
        }

        int? serverCtx = null;
        if (root.TryGetProperty("default_generation_settings", out var dgs)
            && dgs.TryGetProperty("n_ctx", out var nCtxNested)
            && nCtxNested.TryGetInt32(out var ctxNested))
        {
            serverCtx = ctxNested;
        }
        else if (root.TryGetProperty("n_ctx", out var nCtx)
            && nCtx.TryGetInt32(out var ctx))
        {
            serverCtx = ctx;
        }

        if (serverCtx is > 0)
        {
            config.Llm.ContextSize = serverCtx.Value;
            Log.Debug($"Detected context size from /props: {serverCtx}");
        }

        if (!string.IsNullOrWhiteSpace(name)) return;
    }
    catch (Exception ex)
    {
        Log.Debug($"/props unavailable ({ex.GetType().Name}); trying /v1/models");
    }

    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var json = await http.GetStringAsync($"{config.Llm.Endpoint.TrimEnd('/')}/v1/models");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("data", out var data)
            && data.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var idEl)
                    && idEl.ValueKind == System.Text.Json.JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(idEl.GetString()))
                {
                    var name = idEl.GetString()!;
                    config.Llm.Model = name;
                    Log.Debug($"Detected model from /v1/models: {name}");
                    return;
                }
            }
        }
    }
    catch (Exception ex)
    {
        Log.Debug($"/v1/models unavailable ({ex.GetType().Name}); using configured model {config.Llm.Model}");
    }
}

static string DisplayNameFromPath(string modelPath)
{

    var basename = Path.GetFileNameWithoutExtension(modelPath);
    if (string.IsNullOrWhiteSpace(basename)) return modelPath;

    return System.Text.RegularExpressions.Regex.Replace(
        basename,
        @"-(ud-|il-)?(q\d+_[^-]*|f16|f32|bf16|fp16)$",
        "",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

static string? ResolveRefDirectory(AppConfig config)
{

    var candidates = new[]
    {
        "/ref",
        Path.Combine(config.WorkingDirectory, "ref"),
        Environment.GetEnvironmentVariable("OPENMONO_REF_DIR"),
    };

    foreach (var path in candidates)
    {
        if (path is not null && Directory.Exists(path)
            && Directory.EnumerateFileSystemEntries(path).Any())
            return Path.GetFullPath(path);
    }

    return null;
}

static async Task AutoDetectCodeGraphAsync(AppConfig config, IRenderer renderer)
{

    if (config.McpServers.ContainsKey("code-graph"))
        return;

    try
    {

        var (exitCode, _, _) = await ProcessRunner.RunAsync(
            "code-review-graph --version", config.WorkingDirectory, timeoutMs: 5000);
        if (exitCode != 0)
            return;

        var graphDbPaths = new[]
        {
            Path.Combine(config.DataDirectory, "graph-db"),
            Path.Combine(config.WorkingDirectory, ".code-review-graph"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".code-review-graph"),
        };

        var hasGraph = graphDbPaths.Any(p => Directory.Exists(p) && Directory.EnumerateFileSystemEntries(p).Any());

        if (!hasGraph)
        {
            renderer.WriteInfo("code-review-graph found but no graph built. Run: code-review-graph build --repo .");
            return;
        }

        config.McpServers.TryAdd("code-graph", new McpServerSettings
        {
            Command = "code-review-graph",
            Args = ["serve"],
            Enabled = true,
        });

        renderer.WriteInfo("code-review-graph detected — registering as MCP server.");
    }
    catch (Exception ex)
    {
        Log.Debug($"code-review-graph autodetect failed: {ex.Message}");
    }
}
