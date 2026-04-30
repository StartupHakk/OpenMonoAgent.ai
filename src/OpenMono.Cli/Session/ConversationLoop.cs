using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenMono.Config;
using OpenMono.History;
using OpenMono.Hooks;
using OpenMono.Llm;
using OpenMono.Memory;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Tools;
using OpenMono.Tui;
using OpenMono.Utils;

namespace OpenMono.Session;

public sealed class ConversationLoop : IDisposable
{
    private readonly ILlmClient _llm;
    private readonly ToolRegistry _tools;
    private readonly PermissionEngine _permissions;
    private readonly IOutputSink _output;
    private readonly IInputReader _input;
    private readonly ILiveFeedback? _liveFeedback;
    private readonly AppConfig _config;
    private readonly SessionState _session;
    private readonly Compactor _compactor;
    private readonly Checkpointer _checkpointer;
    private readonly MemoryStore? _memoryStore;
    private readonly HookRunner _hookRunner;
    private readonly TurnJournal _journal;
    private readonly CursorStore _cursorStore;
    private readonly ToolResultCache _cache;
    private readonly ArtifactStore _artifactStore;
    private readonly PauseController? _pauseController;

    private readonly List<string> _recentToolSignatures = [];
    private const int DoomLoopThreshold = 3;

    private const int LargeResultThreshold = 20_000;

    public ConversationLoop(
        ILlmClient llm,
        ToolRegistry tools,
        PermissionEngine permissions,
        IOutputSink output,
        IInputReader input,
        ILiveFeedback? liveFeedback,
        AppConfig config,
        SessionState session,
        Compactor? compactor = null,
        MemoryStore? memoryStore = null,
        HookRunner? hookRunner = null,
        TurnJournal? journal = null,
        ToolResultCache? cache = null,
        ArtifactStore? artifactStore = null,
        PauseController? pauseController = null,
        Checkpointer? checkpointer = null)
    {
        _llm = llm;
        _tools = tools;
        _permissions = permissions;
        _output = output;
        _input = input;
        _liveFeedback = liveFeedback;
        _config = config;
        _session = session;
        _compactor = compactor ?? new Compactor(llm, config.Llm.ContextSize);
        _checkpointer = checkpointer ?? new Checkpointer(llm, config.Llm.ContextSize);
        _memoryStore = memoryStore;
        _hookRunner = hookRunner ?? new HookRunner(config, msg => _output.WriteWarning(msg));
        _journal = journal ?? TurnJournal.ForSession(session, config);
        _cursorStore = new CursorStore();
        _cache = cache ?? new ToolResultCache();
        _artifactStore = artifactStore ?? ArtifactStore.ForSession(session, config.DataDirectory);
        _pauseController = pauseController;
    }

    public void Dispose()
    {
        _journal.Dispose();
        _cache.Dispose();
        _artifactStore.Dispose();
    }

    public async Task RunTurnAsync(string userInput, CancellationToken ct)
    {
        _session.AddMessage(new Message { Role = MessageRole.User, Content = userInput });
        _session.TurnCount++;
        _liveFeedback?.BeginTurn();

        try
        {

        var parentMsgId = _session.Messages.Count > 1
            ? _session.Messages[^2].ToolCallId ?? $"msg_{_session.Messages.Count - 2}"
            : null;
        _journal.StartTurn(_session.TurnCount, parentMsgId, _config.Llm.Model);

        _output.WriteDebug($"[Turn] #{_session.TurnCount} — {_session.Messages.Count} messages, ~{_session.TotalTokensUsed} tokens used");

        var lastPromptTokens = _session.Meta.TokenTracker?.LastPromptTokens ?? 0;

        if (_checkpointer.NeedsCheckpoint(_session, lastPromptTokens))
        {
            _output.WriteInfo("Context window approaching limit. Creating checkpoint...");
            _output.WriteDebug($"[Checkpoint] Triggered — messages={_session.Messages.Count} lastPromptTokens={lastPromptTokens}");
            var cpSw = Stopwatch.StartNew();
            var entry = await _checkpointer.CreateCheckpointAsync(_session, ct);
            cpSw.Stop();
            _output.WriteInfo(
                $"Checkpoint #{_session.Checkpoints.Count} stored — " +
                $"{entry.MessagesCompressed} messages compressed in {cpSw.Elapsed.TotalSeconds:F1}s.");
            _output.WriteDebug($"[Checkpoint] Done — effective window={_checkpointer.BuildContextWindow(_session).Count} messages");
        }

        else if (_compactor.NeedsCompaction(_checkpointer.BuildContextWindow(_session), lastPromptTokens))
        {
            _output.WriteInfo("Context window approaching limit. Compacting conversation...");
            _output.WriteDebug($"[Compact] Triggered — messages={_session.Messages.Count} lastPromptTokens={lastPromptTokens}");
            var compactSw = Stopwatch.StartNew();
            var compacted = await _compactor.CompactAsync(_session, ct);
            compactSw.Stop();
            _session.Messages.Clear();
            foreach (var msg in compacted.Messages)
                _session.AddMessage(msg);
            _output.WriteInfo($"Compacted to {_session.Messages.Count} messages in {compactSw.Elapsed.TotalSeconds:F1}s.");
            _output.WriteDebug($"[Compact] Done — {_session.Messages.Count} messages remaining");
        }

        var toolDefs = _session.Meta.PlanMode
            ? _tools.BuildToolDefinitionsFor(_tools.All.Where(t => t.IsReadOnly).Select(t => t.Name))
            : _tools.BuildToolDefinitions();
        var thinking = _session.Meta.ThinkingEnabled;
        var options = new LlmOptions
        {
            Model = _config.Llm.Model,
            MaxTokens = _config.Llm.MaxOutputTokens,
            TopP = _config.Llm.TopP,
            TopK = _config.Llm.TopK,
            MinP = _config.Llm.MinP,
            RepetitionPenalty = _config.Llm.RepetitionPenalty,

            Temperature = thinking ? 0.6 : _config.Llm.Temperature,
            PresencePenalty = thinking ? 0.0 : _config.Llm.PresencePenalty,
            EnableThinking = thinking,
        };

        var maxIterations = 25;
        for (var i = 0; i < maxIterations; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (i == maxIterations - 2)
            {
                _session.AddMessage(new Message
                {
                    Role = MessageRole.User,
                    Content = "[System: You have 1 iteration remaining. Wrap up your current work and respond to the user.]",
                });
            }

            if (i > 0)
                _output.WriteDebug($"[Turn] Iteration {i + 1}/{maxIterations}");

            var contextWindow = _checkpointer.BuildContextWindow(_session);

            var textBuffer = new StringBuilder();
            var toolCalls = new List<ToolCall>();
            var receivedFirstChunk = false;
            var thinkingStarted = false;
            var thinkingCollapsed = false;
            var thinkingChars = 0;
            var indicatorShown = false;
            var turnTokens = 0;

            var context = BuildToolContext();
            var inFlightTasks = new Dictionary<string, Task<ToolResult>>();
            using var siblingAbortCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var indicatorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var indicatorTask = Task.Delay(500, indicatorCts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled) { _output.ShowWaitingIndicator(); indicatorShown = true; }
            }, TaskScheduler.Default);

            await foreach (var chunk in _llm.StreamChatAsync(contextWindow, toolDefs, options, ct))
            {
                if (!indicatorCts.IsCancellationRequested)
                {
                    indicatorCts.Cancel();
                    if (indicatorShown) _output.ClearWaitingIndicator();
                }

                if (chunk.ThinkingDelta is not null)
                {
                    _output.AppendThinking(chunk.ThinkingDelta);
                    thinkingStarted = true;
                    thinkingChars += chunk.ThinkingDelta.Length;
                    continue;
                }

                if (!receivedFirstChunk)
                {
                    if (thinkingStarted && !thinkingCollapsed)
                    {
                        _output.CollapseThinking(thinkingChars);
                        thinkingCollapsed = true;
                    }
                    _output.StartAssistantResponse();
                    receivedFirstChunk = true;
                }

                if (chunk.TextDelta is not null)
                {
                    textBuffer.Append(chunk.TextDelta);
                    _output.StreamText(chunk.TextDelta);

                    if (_pauseController is not null)
                        await _pauseController.WaitIfPausedAsync(ct);
                }

                if (chunk.ToolCallDelta is not null)
                {
                    var call = chunk.ToolCallDelta;
                    toolCalls.Add(call);

                    var tool = _tools.Resolve(call.Name);
                    if (tool is not null && tool.IsConcurrencySafe && tool.IsReadOnly)
                    {
                        _output.WriteDebug($"[P2.4] Starting {call.Name} while streaming...");
                        inFlightTasks[call.Id] = Task.Run(
                            () => ExecuteSingleToolAsync(call, tool, context, siblingAbortCts.Token),
                            siblingAbortCts.Token);
                    }
                }

                if (chunk.Usage is not null)
                {
                    _session.TotalTokensUsed += chunk.Usage.TotalTokens;
                    _session.Meta.TokenTracker?.RecordUsage(chunk.Usage.PromptTokens, chunk.Usage.CompletionTokens);
                    turnTokens += chunk.Usage.TotalTokens;
                }

                if (chunk.IsComplete)
                    break;
            }

            if (thinkingStarted && !thinkingCollapsed)
                _output.CollapseThinking(thinkingChars);

            _output.EndAssistantResponse(turnTokens);

            var assistantMsg = new Message
            {
                Role = MessageRole.Assistant,
                Content = textBuffer.Length > 0 ? textBuffer.ToString() : null,
                ToolCalls = toolCalls.Count > 0 ? toolCalls : null,
            };
            _session.AddMessage(assistantMsg);

            if (toolCalls.Count == 0)
            {
                _journal.FinishTurn("text_only");
                return;
            }

            if (DetectDoomLoop(toolCalls))
            {

                await siblingAbortCts.CancelAsync();
                _output.WriteWarning("Doom loop detected: agent is repeating the same tool calls. Stopping.");
                _session.AddMessage(new Message
                {
                    Role = MessageRole.User,
                    Content = "[System: Doom loop detected — you called the same tools 3 times in a row with identical arguments. Stop repeating and try a different approach, or ask the user for help.]",
                });
                _journal.FinishTurn("doom_loop");
                return;
            }

            var results = await ExecuteToolCallsWithInflightAsync(toolCalls, inFlightTasks, context, siblingAbortCts, ct);

            foreach (var (call, result) in toolCalls.Zip(results))
            {

                var content = result.Content;
                if (content.Length > LargeResultThreshold)
                {
                    var refPath = await StoreContentReplacementAsync(call.Name, content, ct);
                    content = $"[Result truncated — {content.Length} chars. Full output stored at: {refPath}]\n" +
                              content[..Math.Min(2000, content.Length)] + "\n... (truncated)";
                }

                _session.AddMessage(new Message
                {
                    Role = MessageRole.Tool,
                    ToolCallId = call.Id,
                    ToolName = call.Name,
                    Content = content,
                });
            }
        }

        _output.WriteWarning($"Reached maximum iteration limit ({maxIterations}). Stopping.");
        _journal.FinishTurn("max_iterations");
        }
        finally
        {
            _liveFeedback?.EndTurn();
        }
    }

    private bool DetectDoomLoop(List<ToolCall> currentCalls)
    {
        var signature = string.Join("|", currentCalls.Select(c => $"{c.Name}:{c.Arguments}"));
        _recentToolSignatures.Add(signature);

        if (_recentToolSignatures.Count < DoomLoopThreshold)
            return false;

        var recent = _recentToolSignatures.TakeLast(DoomLoopThreshold).ToList();
        var isDoomLoop = recent.All(s => s == recent[0]);

        if (_recentToolSignatures.Count > DoomLoopThreshold * 2)
            _recentToolSignatures.RemoveRange(0, _recentToolSignatures.Count - DoomLoopThreshold);

        return isDoomLoop;
    }

    private async Task<string> StoreContentReplacementAsync(
        string toolName, string content, CancellationToken ct)
    {
        var dir = Path.Combine(_config.DataDirectory, "content-cache");
        Directory.CreateDirectory(dir);

        var guid = Guid.NewGuid().ToString("N")[..8];
        var fileName = $"{toolName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{guid}.txt";
        var path = Path.Combine(dir, fileName);
        await File.WriteAllTextAsync(path, content, ct);
        return path;
    }

    private async Task<List<ToolResult>> ExecuteToolCallsAsync(
        List<ToolCall> toolCalls,
        ToolContext context,
        CancellationToken ct)
    {
        var readOnly = new List<(int Index, ToolCall Call, ITool Tool)>();
        var writeable = new List<(int Index, ToolCall Call, ITool Tool)>();

        for (var i = 0; i < toolCalls.Count; i++)
        {
            var call = toolCalls[i];
            var tool = _tools.Resolve(call.Name);
            if (tool is null)
            {
                writeable.Add((i, call, null!));
                continue;
            }

            if (tool.IsConcurrencySafe && tool.IsReadOnly)
                readOnly.Add((i, call, tool));
            else
                writeable.Add((i, call, tool));
        }

        var results = new ToolResult[toolCalls.Count];

        if (readOnly.Count > 0)
        {
            await Task.WhenAll(readOnly.Select(async item =>
            {
                var result = await ExecuteSingleToolAsync(item.Call, item.Tool, context, ct);
                results[item.Index] = result;
            }));
        }

        foreach (var item in writeable)
        {
            if (item.Tool is null)
            {
                results[item.Index] = ToolResult.Error($"Unknown tool: {item.Call.Name}");
                continue;
            }

            results[item.Index] = await ExecuteSingleToolAsync(item.Call, item.Tool, context, ct);
        }

        return [.. results];
    }

    private async Task<List<ToolResult>> ExecuteToolCallsWithInflightAsync(
        List<ToolCall> toolCalls,
        Dictionary<string, Task<ToolResult>> inFlightTasks,
        ToolContext context,
        CancellationTokenSource siblingAbortCts,
        CancellationToken ct)
    {
        var results = new ToolResult[toolCalls.Count];
        var readOnlyPending = new List<(int Index, ToolCall Call, ITool Tool)>();
        var writeable = new List<(int Index, ToolCall Call, ITool Tool)>();

        for (var i = 0; i < toolCalls.Count; i++)
        {
            var call = toolCalls[i];
            var tool = _tools.Resolve(call.Name);

            if (tool is null)
            {
                writeable.Add((i, call, null!));
                continue;
            }

            if (tool.IsConcurrencySafe && tool.IsReadOnly)
            {

                if (!inFlightTasks.ContainsKey(call.Id))
                {
                    readOnlyPending.Add((i, call, tool));
                }
            }
            else
            {
                writeable.Add((i, call, tool));
            }
        }

        foreach (var item in readOnlyPending)
        {
            inFlightTasks[item.Call.Id] = Task.Run(
                () => ExecuteSingleToolAsync(item.Call, item.Tool, context, siblingAbortCts.Token),
                siblingAbortCts.Token);
        }

        var failedAny = false;
        foreach (var call in toolCalls)
        {
            if (!inFlightTasks.TryGetValue(call.Id, out var task))
                continue;

            var index = toolCalls.IndexOf(call);
            try
            {
                results[index] = await task;

                if (results[index].Class == ResultClass.Crash && !failedAny)
                {
                    failedAny = true;
                    _output.WriteDebug($"[P2.4] {call.Name} crashed — aborting sibling tasks");
                    await siblingAbortCts.CancelAsync();
                }
            }
            catch (OperationCanceledException) when (siblingAbortCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {

                results[index] = ToolResult.Cancelled($"{call.Name} cancelled (sibling abort)");
            }
            catch (Exception ex)
            {
                results[index] = ToolResult.Crash($"{call.Name} crashed: {ex.Message}", "Try with different parameters");
            }
        }

        foreach (var item in writeable)
        {

            ct.ThrowIfCancellationRequested();

            if (item.Tool is null)
            {
                results[item.Index] = ToolResult.Error($"Unknown tool: {item.Call.Name}");
                continue;
            }

            results[item.Index] = await ExecuteSingleToolAsync(item.Call, item.Tool, context, ct);
        }

        return [.. results];
    }

    private async Task<ToolResult> ExecuteSingleToolAsync(
        ToolCall call, ITool tool, ToolContext context, CancellationToken ct)
    {

        _journal.RecordToolCallReceived(call.Id, call.Name, call.Arguments);

        JsonElement input;
        try
        {
            input = JsonDocument.Parse(call.Arguments).RootElement;
        }
        catch (JsonException ex)
        {
            _journal.RecordSchemaRejected(call.Id, $"json_parse: {ex.Message}");
            return ToolResult.Error(
                $"Invalid JSON arguments for {call.Name}: {ex.Message}\nRaw: {call.Arguments[..Math.Min(200, call.Arguments.Length)]}");
        }

        var validationError = ValidateToolInput(tool, input);
        if (validationError is not null)
        {
            _journal.RecordSchemaRejected(call.Id, validationError);
            _output.WriteToolDenied(call.Name, validationError);
            Log.Warn($"Tool schema rejected: {call.Name} — {validationError}");
            return ToolResult.Error(validationError);
        }
        _journal.RecordSchemaValidated(call.Id);

        var sanityError = SanityCheck.Check(call.Name, input, _config.WorkingDirectory);
        if (sanityError is not null)
        {
            _journal.RecordSanityRejected(call.Id, sanityError);
            _output.WriteToolDenied(call.Name, sanityError);
            Log.Warn($"Tool sanity-rejected: {call.Name} — {sanityError}");
            return ToolResult.Error(sanityError);
        }
        _journal.RecordSanityChecked(call.Id);

        if (_session.Meta.PlanMode && !tool.IsReadOnly)
        {
            var planModeError = $"Plan mode is active — only read-only tools are allowed. " +
                                $"Call ExitPlanMode first to make changes with {call.Name}.";
            _journal.RecordPermissionDecided(call.Id, false, "plan_mode_active");
            _output.WriteToolDenied(call.Name, planModeError);
            Log.Info($"Tool blocked by plan mode: {call.Name}");
            return ToolResult.Error(planModeError);
        }

        var capabilities = tool.RequiredCapabilities(input);
        bool allowed;
        string? reason;

        if (capabilities.Count > 0)
        {

            var capDecision = await _permissions.CheckCapabilitiesAsync(tool.Name, capabilities, ct);
            allowed = capDecision.Allowed;
            reason = capDecision.Reason;
        }
        else
        {

            var permLevel = tool.RequiredPermission(input);
            var legacyDecision = await _permissions.CheckAsync(tool.Name, input, permLevel, ct);
            allowed = legacyDecision.Allowed;
            reason = legacyDecision.Reason;
        }

        if (!allowed)
        {
            _journal.RecordPermissionDecided(call.Id, false, reason);
            _output.WriteToolDenied(call.Name, reason ?? "Permission denied");
            Log.Info($"Tool denied: {call.Name} — {reason ?? "User denied"}");
            return ToolResult.Error($"Permission denied for {call.Name}: {reason ?? PermissionEngine.PermissionDeniedOnce}");
        }
        _journal.RecordPermissionDecided(call.Id, true);

        if (tool.IsReadOnly && _cache.TryGet(call.Name, input, out var cachedResult) && cachedResult is not null)
        {
            _journal.RecordToolStarted(call.Id);
            _journal.RecordToolCompleted(call.Id, cachedResult.Class, cachedResult.Artifacts.Select(a => a.Id).ToList());
            _output.WriteToolStart(call.Name, call.Arguments);
            _output.WriteToolSuccess(call.Name);
            Log.Debug($"Tool cache hit: {call.Name}");
            return cachedResult with { ModelPreview = $"[cached] {cachedResult.ModelPreview}" };
        }

        _output.WriteToolStart(call.Name, call.Arguments);

        _session.Meta.TokenTracker?.RecordToolUse(call.Name);

        _journal.RecordToolStarted(call.Id);

        try
        {

            await _hookRunner.RunPreToolUseHooksAsync(call.Name, call.Arguments, ct);

            Log.Debug($"Tool executing: {call.Name}");
            var result = await tool.ExecuteAsync(input, context, ct);

            await _hookRunner.RunPostToolUseHooksAsync(call.Name, result.Content, ct);

            if (result.Class == ResultClass.Success && result.ModelPreview.Length > _artifactStore.LargeOutputThreshold)
            {
                result = _artifactStore.PersistAndReplace(result, call.Name);
                Log.Debug($"Tool output persisted as artifact: {call.Name} ({result.Artifacts.Count} artifacts)");
            }

            if (tool.IsReadOnly && result.Class == ResultClass.Success)
            {
                _cache.Put(call.Name, input, result);
            }

            if (!tool.IsReadOnly && call.Name is "FileWrite" or "FileEdit" or "ApplyPatch")
            {
                if (input.TryGetProperty("file_path", out var pathEl) && pathEl.GetString() is { } filePath)
                {
                    var resolvedPath = Path.GetFullPath(filePath, _config.WorkingDirectory);
                    _cache.InvalidatePath(resolvedPath);
                    FileReadTool.InvalidateCache(resolvedPath);
                }
            }

            var artifactIds = result.Artifacts.Select(a => a.Id).ToList();
            _journal.RecordToolCompleted(call.Id, result.Class, artifactIds);

            if (result.IsError)
            {
                _output.WriteToolError(call.Name, result.ErrorMessage ?? "Unknown error");
                Log.Warn($"Tool error: {call.Name} — {result.ErrorMessage}");
            }
            else
            {
                _output.WriteToolSuccess(call.Name);
                if (result.Diff is not null)
                    _output.WriteToolDiff(result.Diff);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            _journal.RecordToolCrashed(call.Id, "OperationCanceledException", "cancelled");
            Log.Info($"Tool cancelled: {call.Name}");
            return ToolResult.Cancelled($"{call.Name} was cancelled");
        }
        catch (Exception ex)
        {
            _journal.RecordToolCrashed(call.Id, ex.GetType().Name, ex.Message);
            _output.WriteToolError(call.Name, ex.Message);
            Log.Error($"Tool exception: {call.Name}", ex);
            return ToolResult.Crash($"Tool execution failed: {ex.Message}", "Try with different parameters or report this as a bug.");
        }
    }

    private static string? ValidateToolInput(ITool tool, JsonElement input)
        => SchemaValidator.Validate(tool.Name, tool.InputSchema, input);

    private ToolContext BuildToolContext() => new()
    {
        ToolRegistry = _tools,
        Session = _session,
        Permissions = _permissions,
        Config = _config,
        WorkingDirectory = _config.WorkingDirectory,
        WriteOutput = text => _output.WriteMarkdown(text),
        AskUser = (question, ct) => _input.AskUserAsync(question, ct),
        FileHistory = _session.Meta.FileHistory,
        Cursors = _cursorStore,
    };
}
