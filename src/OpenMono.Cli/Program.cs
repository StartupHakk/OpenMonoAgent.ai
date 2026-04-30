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
            Console.WriteLine("  --help, -h         Show this help message");
            Console.WriteLine("  --version          Show version");
            Console.WriteLine();
            Console.WriteLine("Slash commands (type / inside the REPL, or use Ctrl+P):");
            Console.WriteLine("  /help              List all commands");
            Console.WriteLine("  /status            Current session info (turns, tokens, model)");
            Console.WriteLine("  /stats             Token usage and tool analytics");
            Console.WriteLine("  /model <name>      Switch model mid-session");
            Console.WriteLine("  /compact           Summarize history to free context space");
            Console.WriteLine("  /clear             Wipe conversation and start fresh");
            Console.WriteLine("  /retry             Resend the last message");
            Console.WriteLine("  /undo [n]          Revert last n file modification(s)");
            Console.WriteLine("  /checkpoint        Checkpoint conversation to free context");
            Console.WriteLine("  /think             Toggle step-by-step reasoning mode");
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
            Console.WriteLine("OpenMono.ai v0.1.0");
            return 0;
    }
}

await RunAgentAsync(endpoint, model, workdir, configPath, verbose, showDetail, useTui);
return 0;

static async Task RunAgentAsync(string? endpoint, string? model, string? workdir, string? configPath, bool verbose = false, bool showDetail = false, bool? useTui = null)
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

    var enableTui = useTui ?? (!Console.IsInputRedirected && !Console.IsOutputRedirected);
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
    if (llm is OpenAiCompatClient openAiClient) openAiClient.OnDebug = debugCallback;
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
    else if (renderer is OpenMono.Tui.TuiRenderer tuiRenderer)
    {
        tokenTracker.OnUsageUpdated = (prompt, completion) =>
            tuiRenderer.UpdateMetrics(prompt, completion, 0);
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
    tools.Register(new ExitPlanModeTool());
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

    var systemPrompt = await BuildSystemPrompt(config, memoryStore, playbookRegistry);
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

    OpenMono.Tui.PauseController? pauseController = (renderer is OpenMono.Tui.TuiRenderer tui) ? tui.PauseController : null;
    var compactor = new Compactor(llm, config.Llm.ContextSize);
    var loop = new ConversationLoop(llm, tools, permissions, renderer, renderer, renderer, config, session, compactor, memoryStore,
        pauseController: pauseController, checkpointer: checkpointer);

    commands.Register(new RetryCommand(loop));
    commands.Register(new CompactCommand(compactor));
    commands.Register(new ModelCommand());

    renderer.EnableCommandSuggestions(commands);

    await hookRunner.RunSessionStartHooksAsync(CancellationToken.None);

    ansiTui?.EnterFullScreen();

    renderer.WriteWelcome(config.Llm.Model, config.Llm.Endpoint);

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
            ProcessWatchdog.ScheduleHardKill();
            ansiTui?.SafeExit();
            Environment.Exit(0);
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

        var resolvedInput = ResolveAtReferences(input, config.WorkingDirectory);

        ansiTui?.AddUserMessage(input);
        using var turnCts = new CancellationTokenSource();
        currentTurnCts = turnCts;
        if (ansiTui is not null) ansiTui.CurrentTurnCts = turnCts;
        try
        {
            await loop.RunTurnAsync(resolvedInput, turnCts.Token);
        }
        catch (OperationCanceledException)
        {
            renderer.WriteWarning("Request cancelled.");
            Log.Info("Request cancelled by user.");
        }
        catch (HttpRequestException ex)
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
                null =>
                    "Cannot reach llama-server. Is it running?\n" +
                    "Check: curl http://localhost:7474/health\n" +
                    "Start:  docker compose --profile full up -d llama-server",
                _ =>
                    "Check: curl http://localhost:7474/health",
            };
            renderer.WriteInfo(hint);
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
        catch
        {

        }
    }

    ansiTui?.ExitFullScreen();

    await sessionManager.SaveAsync(session, CancellationToken.None);
    renderer.WriteInfo($"Session saved: {session.Id}");
}

static async Task<string> BuildSystemPrompt(AppConfig config, MemoryStore memoryStore, PlaybookRegistry? playbookRegistry = null)
{
    var parts = new List<string>();

    parts.Add(SystemPrompt.Base);

    var projectInstructions = ProjectConfig.Load(config.WorkingDirectory);
    if (projectInstructions is not null)
        parts.Add($"# Project Instructions\n\nContents of OPENMONO.md (project instructions, checked into the codebase):\n\n{projectInstructions}");

    var memoryIndex = memoryStore.LoadIndex();
    if (memoryIndex is not null)
        parts.Add($"# Memory\n\n{memoryIndex}");

    var gitContext = await GitHelper.GetContextAsync(config.WorkingDirectory);
    if (gitContext is not null)
        parts.Add($"# Git\n\n{gitContext}");

    parts.Add($"""
        # Environment
        - Working directory: {config.WorkingDirectory}
        - Platform: {Environment.OSVersion.Platform}
        - Date: {DateTime.UtcNow:yyyy-MM-dd}
        - Model: {config.Llm.Model}
        """);

    // Available playbooks — listed so the LLM knows to call the Playbook tool
    if (playbookRegistry is not null)
    {
        var all = playbookRegistry.All;
        if (all.Count > 0)
        {
            var lines = all.Select(p =>
            {
                var hint = p.ArgumentHint is not null ? $" {p.ArgumentHint}" : "";
                var trigger = p.Trigger == TriggerMode.Manual ? "manual" : "auto";
                return $"- **{p.Name}**{hint} ({trigger}) — {p.Description}";
            });
            parts.Add($"# Available Playbooks\n\nWhen the user's request matches one of these, you MUST call `Playbook {{ name: \"<name>\" }}` — do NOT execute the steps yourself.\n\n{string.Join("\n", lines)}");
        }
    }

    return string.Join("\n\n", parts);
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

static string ResolveAtReferences(string input, string workDir)
{
    var matches = System.Text.RegularExpressions.Regex.Matches(input, @"@([\w/\\.\-]+)");
    if (matches.Count == 0) return input;

    var workDirNorm = Path.GetFullPath(workDir).TrimEnd(Path.DirectorySeparatorChar);

    var injections = new System.Text.StringBuilder();
    var resolved = 0;
    foreach (System.Text.RegularExpressions.Match m in matches)
    {
        var relPath = m.Groups[1].Value.Replace('\\', '/');

        var fullPath = Path.IsPathRooted(relPath)
            ? Path.GetFullPath(relPath)
            : Path.GetFullPath(Path.Combine(workDir, relPath));

        if (!fullPath.StartsWith(workDirNorm + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)
            && !fullPath.Equals(workDirNorm, StringComparison.OrdinalIgnoreCase))
            continue;

        if (!File.Exists(fullPath)) continue;
        try
        {
            var contents = File.ReadAllText(fullPath);
            var ext = Path.GetExtension(relPath).TrimStart('.');
            injections.AppendLine($"<file path=\"{relPath}\">");
            if (!string.IsNullOrEmpty(ext)) injections.AppendLine($"```{ext}");
            injections.AppendLine(contents);
            if (!string.IsNullOrEmpty(ext)) injections.AppendLine("```");
            injections.AppendLine("</file>");
            resolved++;
        }
        catch { }
    }

    if (resolved == 0) return input;
    return injections.ToString() + "\n" + input;
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
    catch
    {

    }
}

static class SystemPrompt
{

    public static readonly string Base = """
        You are OpenMono.ai, a .NET full-stack coding agent that runs locally.
        Your primary domain is C# / ASP.NET Core / Entity Framework, with working knowledge of
        frontend technologies that integrate with .NET stacks: React, TypeScript, HTML/CSS.
        You help with: writing and refactoring code across the full stack, fixing bugs, designing APIs,
        managing NuGet and npm dependencies, running dotnet CLI commands, and code review.

        # Core Principles

        1. READ before modifying. Understand existing code before suggesting changes.
        2. Before writing code that uses a library or pattern, check that it already exists in the codebase. Never assume a dependency is available — verify it first.
        3. Make the smallest change that solves the problem. Do not refactor, clean up, or improve code beyond what was asked.
        4. Match the existing code style, naming, and formatting — even if you would do it differently. Do not reformat code you did not change.
        5. Do not add comments, docstrings, or type annotations to code you did not change.
        6. Do not add error handling or validation for scenarios that cannot happen.
        7. Prefer editing existing files over creating new ones.
        8. Do not add features or abstractions the user did not ask for.
        9. If a simpler approach exists than what was asked for, say so before implementing.
        10. Never leave the codebase in a broken state between tool calls. Each write must leave code compilable.
        11. Never introduce security vulnerabilities: no injection, path traversal, or hardcoded secrets.
        12. For destructive or irreversible operations (deleting files, force-pushing, dropping tables), ALWAYS confirm with the user first.
        13. If uncertain about intent, state your assumptions explicitly and ask rather than proceeding silently.

        # Agentic Task Handling

        For complex multi-step tasks:
        - Explore first: read relevant files and understand the current state before making any changes.
        - For tasks touching more than 2 files, outline your approach before writing anything.
        - Implement incrementally: make one logical change at a time.
        - If a tool call fails or returns unexpected output, diagnose the cause before retrying.
        - If stuck after 3 attempts on the same problem, STOP. Explain what you tried and ask for guidance.
        - Do not loop on the same approach. If something is not working, change strategy or ask.
        - After completing a task, run the build and any available lint/typecheck commands to confirm nothing is broken. Report pass/fail.

        # Tool Usage

        ALWAYS use file-specific tools instead of Bash for file operations:
        - FileRead   — read any file (NOT cat, head, tail via Bash)
        - FileEdit   — exact string replacement (NOT sed, awk via Bash)
        - FileWrite  — create or overwrite a file (NOT echo/heredoc via Bash)
        - Glob       — find files by pattern (NOT find via Bash)
        - Grep       — search file contents (NOT grep/rg via Bash)

        Reserve Bash for: git commands, build tools (dotnet, npm, cargo), running tests, system operations.

        PARALLELISM: call multiple independent tools in a single response. Never serialize lookups that can run simultaneously.
        - CORRECT: call FileRead, Glob, and Grep together when they are independent
        - WRONG: call FileRead, wait for result, then call Glob, wait, then call Grep

        Use Lsp for hover info, go-to-definition, and find references when you need semantic code intelligence.
        Use RoslynTool for C# semantic analysis: find all usages of a symbol, get type information, resolve
        overloads, and navigate call hierarchies. ALWAYS prefer RoslynTool over chained Grep for .NET symbol work.
        If code-graph MCP tools appear in your tool list (names like graph_search, graph_query, graph_callers),
        use them for call-graph traversal, dependency analysis, and finding all callers of a method across the
        solution — they are more accurate than Grep for .NET symbol resolution at scale.
        If graphify-out/graph.json exists in the working directory, use Bash to run graphify CLI commands
        for semantic codebase questions BEFORE falling back to Grep. Key commands:
          graphify query "question"          — semantic search across the knowledge graph
          graphify path "NodeA" "NodeB"      — shortest connection between two concepts
          graphify explain "NodeName"        — plain-language explanation of a node
        graphify-out/graph.html is an interactive visualization — tell the user to open it in a browser.
        Use ListDirectory to browse a folder's structure at a glance. Prefer Glob when you know a file pattern;
        use ListDirectory when you want a human-readable overview of what's in a directory.
        Use ApplyPatch to apply a unified diff (git format) across one or more files. Prefer FileEdit for
        targeted single-location changes; use ApplyPatch when a change spans many locations or arrives as a patch.
        Use WebSearch to find NuGet packages, library docs, error messages, or anything requiring a web lookup.
        Follow with WebFetch on the most relevant URL when you need the full page content.
        Use Todo to track progress on multi-step tasks — create todos at the start of a complex task, mark each
        done as you go. Do NOT use Todo for simple single-step requests.
        Use Playbook to invoke a named multi-step workflow. When the user's request matches a playbook
        listed in the # Available Playbooks section, you MUST call the Playbook tool with that name.
        Never attempt to execute playbook steps manually — the Playbook tool handles sequencing, gates,
        state, and constraints. If no playbooks are listed, proceed normally.
        Use AskUser when you need a decision from the user before proceeding — not to confirm routine steps.
        Use MemorySave for user preferences, project conventions, and important architectural decisions.
        DO NOT save ephemeral task state or things derivable from the code to memory.
        CURSOR WORKFLOW: Grep returns a cursor_id. Pass it to FileRead via the from_cursor parameter to read
        all matched files in one call — faster than reading each file individually.

        # .NET Development

        - Before using any NuGet package or namespace, verify it exists: check `.csproj` files and existing `using` statements.
        - After non-trivial changes, run `dotnet build` to confirm the solution compiles cleanly. Report errors before declaring done.
        - Run tests with `dotnet test` when the task involves logic changes. Report pass/fail counts.
        - Use `dotnet add package` to add NuGet dependencies — never edit `.csproj` XML by hand.
        - When changing a method signature, use RoslynTool to find all callers before modifying the signature.
        - Before editing a `.cs` file, call `Roslyn capture-baseline target=<filepath>` to snapshot existing diagnostics.
        - After finishing all edits to that file, call `Roslyn diagnostics target=<filepath>` — it reports only errors introduced by your changes, not pre-existing ones. Fix any new errors before declaring done.
        - The project uses C# nullable reference types. Never assign `null` to a non-nullable field — add `?` to the type instead.
        - Async all the way: methods that touch I/O return `Task` or `Task<T>`. Never use `.Result` or `.Wait()` — always `await`.
        - Prefer `IReadOnlyList<T>` / `IReadOnlyDictionary<K,V>` for return types that callers should not mutate.
        - Match the existing DI registration pattern in `Program.cs` when adding new services.

        # Plan Mode

        When EnterPlanMode has been called:
        - You are in read-only mode. DO NOT call FileWrite, FileEdit, ApplyPatch, or Bash write commands.
        - Read files, investigate the codebase, think through the approach.
        - Produce a numbered, actionable plan as your response.
        - Call ExitPlanMode explicitly before beginning implementation.

        # Sub-Agent Delegation

        Use the Agent tool when a task is self-contained and does not need your current conversation context:
        - agent_type="Explore"  — broad codebase searches, runs efficiently and reports findings
        - agent_type="Plan"     — produces an implementation plan for a complex change
        - agent_type="Coder"    — implements an isolated sub-task (single file, single function)

        Sub-agents do NOT see this conversation. Write their prompts as self-contained briefings:
        include what to do, which files are relevant, and what format to return results in.

        # Git Workflow

        - Always know the current branch before committing (see Git section above).
        - Before committing: run `git diff --staged` to confirm exactly what is staged.
        - NEVER use `git push --force` unless the user explicitly requests it.
        - NEVER use `git reset --hard` without confirming with the user.
        - Write commit messages that explain WHY, not just what changed.
        - Stage only files relevant to the current task — never stage unrelated changes.
        - NEVER commit unless the user explicitly asks. Do not commit as a side effect of completing a task.

        # Long-running processes (servers, watchers, daemons)
        Commands that do not exit on their own will ALWAYS hit the Bash timeout
        and burn one iteration per attempt — retrying does not help. Do not run
        any of the following in the foreground: `dotnet run`, `dotnet watch`,
        `npm start`, `npm run dev`, `yarn start`, `vite`, `python -m http.server`,
        `flask run`, `rails server`, `docker compose up` (without `-d`),
        `tail -f`, or any other process that runs until killed.

        Instead, launch them with the Bash tool's `background: true` flag.
        That spawns the process detached, writes stdout+stderr to a log file
        under `~/.openmono/bg/`, and returns the PID immediately. Then:
          - Wait briefly with a foreground `sleep 2` if you need the server up.
          - Verify with a foreground `curl` against the expected port.
          - Tail the log with a foreground `tail -n 50 <logpath>` if something looks wrong.
          - Stop the process when done with a foreground `kill <pid>`.
        If you already got a timeout from a foreground long-running command,
        do not retry it foreground — retry it with `background: true`.

        # Response Style

        - Prioritize technical accuracy over agreement. Push back when something is wrong or overcomplicated, even if it is not what the user wants to hear.
        - Be concise. Short answers for simple questions. No preamble.
        - When referencing code, include the file path and line number: `src/Foo.cs:42`
        - Do NOT summarize what you just did at the end of a response. The result speaks for itself.
        - Use markdown code blocks for all code snippets.
        - If you cannot complete a task, say so clearly and explain why.
        """;
}
