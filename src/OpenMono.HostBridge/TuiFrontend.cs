using OpenMono.Config;
using OpenMono.Permissions;
using OpenMono.Playbooks;
using OpenMono.Rendering;
using OpenMono.Session;

namespace OpenMono.HostBridge;

/// <summary>
/// The <see cref="ITerminal"/> the full-screen agent renderer needs. The CLI's
/// own implementation is internal, so the bridge carries this small equivalent:
/// real window size, direct console writes, non-blocking key polls.
/// </summary>
internal sealed class BridgeTerminal : ITerminal
{
    public int WindowWidth => SafeDimension(() => Console.WindowWidth, 120);
    public int WindowHeight => SafeDimension(() => Console.WindowHeight, 40);
    public bool IsOutputRedirected => Console.IsOutputRedirected;

    public event Action<ConsoleSpecialKey>? InterruptRequested;

    public BridgeTerminal()
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            InterruptRequested?.Invoke(e.SpecialKey);
        };
    }

    public ValueTask WriteAsync(string value, CancellationToken ct = default)
    {
        Console.Out.Write(value);
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteLineAsync(string value, CancellationToken ct = default)
    {
        Console.Out.WriteLine(value);
        return ValueTask.CompletedTask;
    }

    public ConsoleKeyInfo? TryReadKey() =>
        Console.KeyAvailable ? Console.ReadKey(intercept: true) : null;

    public async ValueTask<ConsoleKeyInfo> ReadKeyAsync(CancellationToken ct = default)
    {
        while (!ct.IsCancellationRequested)
        {
            var key = TryReadKey();
            if (key.HasValue)
                return key.Value;
            await Task.Delay(20, ct);
        }
        return default;
    }

    private static int SafeDimension(Func<int> read, int fallback)
    {
        try
        {
            var value = read();
            return value > 0 ? value : fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}

/// <summary>
/// The existing full-screen agent TUI (<see cref="AnsiTuiRenderer"/>), driven
/// against the ACP session instead of an in-process loop. Streaming text, tool
/// cards, thinking, permission menus, and the PLAN/BUILD chrome are all the
/// real renderer — the model loop just happens to live in the container.
/// Slash commands (/mode, /build, /plan, /compact, /think, /playbook) are sent
/// to the server, which already handles them; /quit, /exit, and /clear run
/// locally.
/// </summary>
internal sealed class TuiFrontend
{
    private readonly AnsiTuiRenderer _tui;
    private readonly AppConfig _config;
    private readonly SessionState _session;
    private bool _responseOpen;

    public TuiFrontend(string model, string workDir)
    {
        _config = new AppConfig();
        // The @-menu searches config.WorkingDirectory (AnsiSuggestionOverlay):
        // point it at the bridge workdir explicitly instead of inheriting
        // whatever cwd/env the process happened to start with.
        if (!string.IsNullOrWhiteSpace(workDir))
            _config.WorkingDirectory = workDir;
        _session = new SessionState { Model = model ?? "" };
        _session.Meta.HostEnabled = true;
        _tui = new AnsiTuiRenderer(_config, _session, new BridgeTerminal());
    }

    public bool BuildMode
    {
        get => !_session.Meta.PlanMode;
        set => _session.Meta.PlanMode = !value;
    }

    public void SetTokens(long totalUsed) => _session.TotalTokensUsed = (int)Math.Min(totalUsed, int.MaxValue);

    /// <summary>
    /// Feeds the status-bar context gauge. It reads
    /// <c>TokenTracker.LastPromptTokens / AppConfig.Llm.ContextSize</c> — so
    /// the bridge must feed both: the tracker's display-only estimate (never
    /// touches cumulative cost totals) and the window the server reported.
    /// </summary>
    public void SetUsage(long contextTokens, long contextWindow)
    {
        _session.Meta.TokenTracker ??= new TokenTracker();
        if (contextTokens > 0)
            _session.Meta.TokenTracker.SetEstimatedPromptTokens((int)Math.Min(contextTokens, int.MaxValue));
        if (contextWindow > 0)
            _config.Llm.ContextSize = (int)Math.Min(contextWindow, int.MaxValue);
    }

    /// <summary>
    /// Commits streamed prose before any committed render, keeping the
    /// transcript chronological (see <c>AnsiPainter.CommitStreamSegment</c>).
    /// </summary>
    public void FlushResponse() => _tui.CommitStreamSegment();

    public void Enter(string model, string endpoint, string statusLine)
    {
        TuiInputFilter.DrainStdin();
        _tui.EnterFullScreen();
        _tui.WriteWelcome(model, endpoint);
        _tui.WriteInfo(statusLine);
    }

    public void AddUserMessage(string text) => _tui.AddUserMessage(text);

    public void Exit() => _tui.ExitFullScreen();

    public void BeginTurn() => _tui.BeginTurn();
    public void EndTurn() => _tui.EndTurn();

    public CancellationTokenSource? CurrentTurnCts
    {
        get => _tui.CurrentTurnCts;
        set => _tui.CurrentTurnCts = value;
    }

    public string ReadInput() => _tui.ReadInput();
    public void ClearConversation() => _tui.ClearConversation();

    /// <summary>
    /// Messages queued while a turn was running (e.g. a pasted paragraph whose
    /// lines arrived separately). The bridge joins them into the next turn so a
    /// paragraph stays one turn instead of scattering across several.
    /// </summary>
    public string? DequeueQueued() => _tui.DequeueMessage();

    public void ResponseBegin()
    {
        if (_responseOpen)
            return;
        _responseOpen = true;
        _tui.StartAssistantResponse();
    }

    public void ResponseEnd()
    {
        if (!_responseOpen)
            return;
        _responseOpen = false;
        _tui.EndAssistantResponse();
    }

    public void StreamText(string content)
    {
        ResponseBegin();
        _tui.StreamText(content);
    }

    public void AppendThinking(string content) => _tui.AppendThinking(content);
    public void ToolStart(string name, string summary) => _tui.WriteToolStart(name, summary);
    public void ToolSuccess(string name) => _tui.WriteToolSuccess(name);
    public void ToolError(string name, string error) => _tui.WriteToolError(name, error);
    public void Info(string message) => _tui.WriteInfo(message);
    public void Error(string message) => _tui.WriteError(message);
    public void Markdown(string markdown) => _tui.WriteMarkdown(markdown);

    public void ModeNotice(string mode)
    {
        BuildMode = !mode.Equals("plan", StringComparison.OrdinalIgnoreCase);
        _tui.WriteInfo($"Mode: {mode}");
    }

    public Task<bool> ConfirmAsync(string question, CancellationToken ct)
    {
        FlushResponse();
        return _tui.AskUserAsync(question + " [y/N]", ct).ContinueWith(
            t => t.Result.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) ||
                 t.Result.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public Task<string> InputAsync(string question, CancellationToken ct)
    {
        FlushResponse();
        return _tui.AskUserAsync(question, ct);
    }

    public Task<(bool Allow, string Scope)> PermissionAsync(string tool, string summary, CancellationToken ct)
    {
        // Defensive fail-closed: the TUI is only constructed on an interactive
        // terminal (see Program.cs useTui), but a redirected stdin here would
        // park ReadPermissionMenu on TryReadKey forever — never block.
        if (Console.IsInputRedirected)
            return Task.FromResult((false, "once"));
        FlushResponse();
        return _tui.AskPermissionAsync(tool, summary, ct).ContinueWith(
            t => t.Result switch
            {
                PermissionResponse.Allow => (true, "once"),
                PermissionResponse.AllowAll => (true, "session"),
                PermissionResponse.Deny => (false, "once"),
                PermissionResponse.DenyAll => (false, "session"),
                _ => (false, "once"),
            },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public Task<bool> PlaybookAsync(
        string name,
        IReadOnlyList<PlaybookStep> steps,
        IReadOnlyList<PlaybookTool> tools,
        bool requiresModeSwitch,
        CancellationToken ct)
    {
        var plan = new PlaybookToolPlan
        {
            PlaybookName = name,
            Steps = steps.Select(s => new PlaybookPlanStep
            {
                Id = s.Id,
                Gate = Enum.TryParse<GateType>(s.Gate, ignoreCase: true, out var gate) ? gate : GateType.None,
                Description = s.Description,
            }).ToList(),
            Tools = tools.Select(t => new PlaybookPlanTool
            {
                Name = t.Name,
                IsReadOnly = t.IsReadOnly,
                Dangerous = t.Dangerous,
            }).ToList(),
            RequiresModeSwitch = requiresModeSwitch,
        };
        FlushResponse();
        return _tui.RequestPlaybookApprovalAsync(plan, ct);
    }

    /// <summary>
    /// Masked sudo-password prompt inside the TUI chat lane. Stays in
    /// fullscreen: the input box shows <c>*</c> only and the secret never
    /// touches the transcript, history, or suggestion overlay.
    /// Falls back to the console prompt when stdin is redirected.
    /// Nothing typed here is logged.
    /// </summary>
    public Task<char[]> PasswordAsync(string prompt, CancellationToken ct)
    {
        FlushResponse();
        if (Console.IsInputRedirected)
            return Task.FromResult(HostIdentity.PromptPassword(prompt, Console.Error));
        try
        {
            return _tui.AskSecretAsync(prompt, ct);
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(Array.Empty<char>());
        }
    }
}

/// <summary>
/// <see cref="IBridgeUi"/> over the full-screen renderer. Bare-metal
/// HOST_EXEC shows as a <c>host-exec</c> tool card; its output stays out of
/// the transcript (it returns to the agent, which summarizes it).
/// </summary>
internal sealed class TuiUi : IBridgeUi
{
    private readonly TuiFrontend _tui;

    public TuiUi(TuiFrontend tui) => _tui = tui;

    public void ResponseBegin() => _tui.ResponseBegin();
    public void ResponseEnd() => _tui.ResponseEnd();
    public void Text(string content) => _tui.StreamText(content);
    public void Thinking(string content) => _tui.AppendThinking(content);
    public void ToolStart(string name, string summary)
    {
        _tui.FlushResponse();
        _tui.ToolStart(name, summary);
    }

    public void ToolEnd(string name, bool ok, string reason)
    {
        _tui.FlushResponse();
        if (ok)
            _tui.ToolSuccess(name);
        else
            _tui.ToolError(name, reason);
    }

    public void Usage(long contextTokens, long contextWindow) => _tui.SetUsage(contextTokens, contextWindow);
    public void Info(string message)
    {
        _tui.FlushResponse();
        _tui.Info(message);
    }
    public void Error(string message)
    {
        _tui.FlushResponse();
        _tui.Error(message);
    }
    public void Markdown(string markdown)
    {
        _tui.FlushResponse();
        _tui.Markdown(markdown);
    }

    public void HostStart(string command)
    {
        _tui.FlushResponse();
        _tui.ToolStart("host-exec", command);
    }

    public void HostDone(string command, string output)
    {
        _tui.FlushResponse();
        var failed = output.StartsWith("ERROR:", StringComparison.Ordinal) ||
            output.StartsWith("Exit code:", StringComparison.Ordinal);
        if (failed)
            _tui.ToolError("host-exec", FirstLine(output));
        else
            _tui.ToolSuccess("host-exec");
    }

    public void PlaybookDecision(string name, bool allow)
    {
        _tui.FlushResponse();
        _tui.Info($"Playbook '{name}' {(allow ? "approved" : "denied")}.");
    }

    public void ModeNotice(string mode)
    {
        _tui.FlushResponse();
        _tui.ModeNotice(mode);
    }

    public Task<bool> ConfirmAsync(string question, CancellationToken ct) =>
        _tui.ConfirmAsync(question, ct);

    public Task<(bool Allow, string Scope)> PermissionAsync(string tool, string summary, CancellationToken ct) =>
        _tui.PermissionAsync(tool, summary, ct);

    public Task<string> InputAsync(string question, CancellationToken ct) =>
        _tui.InputAsync(question, ct);

    public Task<bool> PlaybookAsync(
        string name,
        IReadOnlyList<PlaybookStep> steps,
        IReadOnlyList<PlaybookTool> tools,
        bool requiresModeSwitch,
        CancellationToken ct) =>
        _tui.PlaybookAsync(name, steps, tools, requiresModeSwitch, ct);

    public Task<char[]> PasswordAsync(string prompt, CancellationToken ct) =>
        _tui.PasswordAsync(prompt, ct);

    private static string FirstLine(string s)
    {
        var idx = s.IndexOf('\n');
        return (idx < 0 ? s : s[..idx]).Trim();
    }
}
