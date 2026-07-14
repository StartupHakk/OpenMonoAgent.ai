using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenMono.Acp;
using OpenMono.Config;
using OpenMono.History;
using OpenMono.Hooks;
using OpenMono.Llm;
using OpenMono.Memory;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Tools;
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
    private readonly IAcpEventSink? _sink;
    private readonly IAcpUserInteraction? _interaction;
    private readonly IToolExecutor _executor;
    private readonly IReadOnlyList<ITool>? _toolSubset;

    private readonly DoomLoopDetector _doomLoop = new();

    private const int LargeResultThreshold = 20_000;

    private readonly int _maxIterations;
    private readonly int _agentDepth;

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
        Checkpointer? checkpointer = null,
        IAcpEventSink? sink = null,
        IToolExecutor? executor = null,
        IReadOnlyList<ITool>? toolSubset = null,
        IAcpUserInteraction? interaction = null,
        int maxIterations = 1000,
        int agentDepth = 0)
    {
        _llm = llm;
        _tools = tools;
        _output = output;
        if (interaction is null)
        {
            _input = input;
            _permissions = permissions;
        }
        else
        {






            var adapter = new AcpInputReaderAdapter(interaction);
            _input = adapter;
            _permissions = new PermissionEngine(config, output, adapter);
        }
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
        _sink = sink;
        _interaction = interaction;



        _executor = executor ?? new LocalToolExecutor(
            _journal,
            _output,
            _config,
            _session,
            _permissions,
            _cache,
            _artifactStore,
            _hookRunner,
            _sink);
        _toolSubset = toolSubset;
        _maxIterations = maxIterations;
        _agentDepth = agentDepth;
    }

    public void Dispose()
    {
        _journal.Dispose();
        _cache.Dispose();
        _artifactStore.Dispose();
    }

    public async Task RunTurnAsync(string userInput, IReadOnlyList<ContentPart>? imageParts, CancellationToken ct)
    {
        _doomLoop.Reset();
        _session.AddMessage(new Message {
            Role = MessageRole.User,
            Content = imageParts is { Count: > 0 }
                ? $"[{imageParts.Count} image(s)] {userInput}"
                : userInput,
            ContentParts = imageParts is { Count: > 0 }
                ? [new TextPart(userInput), .. imageParts]
                : null
        });
        if (imageParts is { Count: > 0 } && !_config.VisionEnabled)
        {
            _output.WriteWarning("Image attached but vision is not enabled — re-run 'openmono setup'.");
            return;
        }
        _session.TurnCount++;
        await RunTurnInternalAsync(ct);
    }






    public Task ContinueTurnAsync(CancellationToken ct) => RunTurnInternalAsync(ct);

    /// <summary>
    /// Resume after a permission pause by actually executing (or, if denied, refusing)
    /// the tool calls from the last assistant message that have not yet been answered,
    /// appending the REAL <see cref="ToolResult"/> for each.
    ///
    /// This replaces the old "Permission granted — re-issue the tool call" handshake.
    /// That handshake never executed the tool: the file was never written, and the model
    /// routinely read "permission granted" as "done" and hallucinated success. Here the
    /// approved tool runs server-side and the model sees ground truth (real output or a
    /// real error). The caller must seed the permission decision (so this execution does
    /// not re-prompt) before invoking this.
    /// </summary>
    public async Task ResolvePendingToolCallsAsync(bool granted, CancellationToken ct)
    {
        var lastAssistant = _session.Messages
            .LastOrDefault(m => m.Role == MessageRole.Assistant && m.ToolCalls is { Count: > 0 });
        if (lastAssistant?.ToolCalls is null)
        {
            Log.Warn("[Resume] No pending tool calls to resolve after permission decision.");
            return;
        }

        var answered = _session.Messages
            .Where(m => m.Role == MessageRole.Tool && m.ToolCallId is not null)
            .Select(m => m.ToolCallId!)
            .ToHashSet();

        var context = BuildToolContext();

        foreach (var call in lastAssistant.ToolCalls)
        {
            if (answered.Contains(call.Id)) continue;

            ToolResult result;
            if (!granted)
            {
                var ctxSummary = LocalToolExecutor.SummarizeToolArgs(call.Arguments);
                result = ToolResult.Error(
                    $"The user DENIED permission for {call.Name}" +
                    (string.IsNullOrEmpty(ctxSummary) ? "" : $" ({ctxSummary})") + ". " +
                    "Do not retry this operation. Briefly tell the user you could not complete it, " +
                    "then ask how they would like to proceed.");
            }
            else
            {
                // Permission was granted; the caller seeded the decision so this does
                // not re-prompt. Execute for real and capture the actual result.
                var tool = _tools.Resolve(call.Name);
                result = await _executor.ExecuteAsync(call, tool, context, ct);
            }

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
                IsError = result.IsError,
            });

            if (_sink is not null)
            {
                if (!granted)
                {
                    // Denied: the tool never executed and ExecuteAsync (which normally emits
                    // status/end) was skipped, so emit them here or the card stays stuck on the
                    // ⏸ awaiting-permission icon. Show a short user-facing note — NOT the
                    // model-directed "do not retry / tell the user…" text that lives in ModelPreview.
                    await _sink.OnToolResultPreviewAsync(call.Id, "Permission denied by user.", null);
                    await _sink.OnToolStatusAsync(call.Id, "failed");
                    await _sink.OnToolEndAsync(call.Id, call.Name, ok: false, durationMs: 0.0);
                }
                else
                {
                    var artifactId = result.Artifacts.Count > 0 ? result.Artifacts[0].Id : null;
                    await _sink.OnToolResultPreviewAsync(call.Id, result.ModelPreview, artifactId);
                }
            }
        }
    }

    private async Task RunTurnInternalAsync(CancellationToken ct)
    {
        _doomLoop.Reset();
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
            await RunCompactionAsync(lastPromptTokens, customInstructions: null, ct);
        }

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

        var maxIterations = _maxIterations;
        for (var i = 0; i < maxIterations; i++)
        {
            ct.ThrowIfCancellationRequested();

            if (i > 0)
            {
                var iterPromptTokens = _session.Meta.TokenTracker?.LastPromptTokens ?? 0;
                if (_checkpointer.NeedsCheckpoint(_session, iterPromptTokens))
                {
                    _output.WriteInfo("Context window approaching limit. Creating checkpoint...");
                    _output.WriteDebug($"[Checkpoint] Triggered mid-turn — messages={_session.Messages.Count}");
                    var cpSw = Stopwatch.StartNew();
                    var entry = await _checkpointer.CreateCheckpointAsync(_session, ct);
                    cpSw.Stop();
                    _output.WriteInfo($"Checkpoint #{_session.Checkpoints.Count} stored — {entry.MessagesCompressed} messages compressed in {cpSw.Elapsed.TotalSeconds:F1}s.");
                    _output.WriteDebug($"[Checkpoint] Done — effective window={_checkpointer.BuildContextWindow(_session).Count} messages");
                    _doomLoop.Reset();
                    i = -1; continue;
                }
                else if (_compactor.NeedsCompaction(_checkpointer.BuildContextWindow(_session), iterPromptTokens))
                {
                    await RunCompactionAsync(iterPromptTokens, customInstructions: null, ct);
                    _doomLoop.Reset();
                    i = -1; continue;
                }
            }

            if (i > 0)
                _output.WriteDebug($"[Turn] Iteration {i + 1}/{maxIterations}");

            var contextWindow = _checkpointer.BuildContextWindow(_session);

            // Recompute the tool set EACH iteration so a mid-turn mode flip (e.g. ImplementPlan
            // switching Plan→Build) immediately changes which tools the model is offered — the
            // banner below and these defs always reflect the same, current mode.
            var allowedToolNames = (_toolSubset?.Select(t => t.Name) ?? _tools.All.Select(t => t.Name)).ToArray();
            var planModeToolNames = allowedToolNames.Where(n => _tools.Resolve(n)?.IsReadOnly == true).ToArray();
            var toolDefs = _session.Meta.PlanMode
                ? _tools.BuildToolDefinitionsFor(planModeToolNames)
                : _tools.BuildToolDefinitionsFor(allowedToolNames);

            // PREPEND the authoritative current-mode banner to the system message every turn
            // (ephemeral, not persisted) so it is the first thing the model reads. This is the
            // SINGLE source of mode-state truth in the prompt — the static system prompt says
            // nothing about the current mode. Both Plan and Build get a banner so the model
            // never infers its mode or parrots a stale "I'm in plan mode" from history.
            {
                // BuildContextWindow may return the session's live message list; mutate a copy,
                // or the banner is persisted and stacks up on every iteration — which also
                // shifts the prompt prefix each call and defeats llama.cpp's KV cache.
                contextWindow = new List<Message>(contextWindow);
                var sysIdx = contextWindow.FindIndex(m => m.Role == MessageRole.System);
                if (sysIdx >= 0)
                {
                    var banner = ModeInstructions.CurrentModeBanner(_session.Meta.PlanMode, planModeToolNames);
                    contextWindow[sysIdx] = contextWindow[sysIdx] with
                    {
                        Content = banner + (contextWindow[sysIdx].Content ?? ""),
                    };
                }
                Log.Info($"[OMA_MODE] turn {_session.TurnCount}: PlanMode={_session.Meta.PlanMode}; " +
                         $"tools offered={(_session.Meta.PlanMode ? planModeToolNames.Length : allowedToolNames.Length)} " +
                         $"({(_session.Meta.PlanMode ? "read-only" : "all")})");
            }

            // Log context window composition for debugging
            var systemMsgs = contextWindow.Count(m => m.Role == MessageRole.System);
            var userMsgs = contextWindow.Count(m => m.Role == MessageRole.User);
            var assistantMsgs = contextWindow.Count(m => m.Role == MessageRole.Assistant);
            var toolMsgs = contextWindow.Count(m => m.Role == MessageRole.Tool);
            Log.Info($"[OMA_CONTEXTWINDOW] Sending to LLM: system={systemMsgs} user={userMsgs} assistant={assistantMsgs} tool={toolMsgs} total={contextWindow.Count}");
            if (systemMsgs > 0)
            {
                var sysMsg = contextWindow.First(m => m.Role == MessageRole.System);
                var preview = sysMsg.Content?.Substring(0, Math.Min(100, sysMsg.Content?.Length ?? 0)) ?? "";
                Log.Info($"[OMA_CONTEXTWINDOW] System message preview: {preview}...");
            }

            var textBuffer = new StringBuilder();
            var toolCalls = new List<ToolCall>();
            var receivedFirstChunk = false;
            var thinkingStarted = false;
            var thinkingCollapsed = false;
            var thinkingChars = 0;
            var indicatorShown = false;
            var turnTokens = 0;
            var requestSw = Stopwatch.StartNew();
            var ttft = TimeSpan.Zero;
            UsageInfo? lastUsage = null;

            var context = BuildToolContext();
            var inFlightTasks = new Dictionary<string, Task<ToolResult>>();
            using var siblingAbortCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var indicatorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var indicatorTask = Task.Delay(500, indicatorCts.Token).ContinueWith(t =>
            {
                if (!t.IsCanceled) { _output.ShowWaitingIndicator(); indicatorShown = true; }
            }, TaskScheduler.Default);

            try
            {
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
                    if (_sink is not null) await _sink.OnThinkingDeltaAsync(chunk.ThinkingDelta);
                    continue;
                }

                if (!receivedFirstChunk)
                {
                    ttft = requestSw.Elapsed;
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
                    if (_sink is not null) await _sink.OnTextDeltaAsync(chunk.TextDelta);
                }

                if (chunk.ToolCallDelta is not null)
                {
                    var call = chunk.ToolCallDelta;
                    toolCalls.Add(call);

                    var tool = _tools.Resolve(call.Name);
                    if (tool is not null && tool.IsConcurrencySafe)
                    {
                        _output.WriteDebug($"[P2.4] Starting {call.Name} while streaming...");


                        inFlightTasks[call.Id] = Task.Run(
                            () => _executor.ExecuteAsync(call, tool, context, siblingAbortCts.Token),
                            siblingAbortCts.Token);
                    }
                }

                if (chunk.Usage is not null)
                {
                    lastUsage = chunk.Usage;
                    _session.TotalTokensUsed += chunk.Usage.TotalTokens;
                    _session.Meta.TokenTracker?.RecordUsage(chunk.Usage.PromptTokens, chunk.Usage.CompletionTokens);
                    _session.Meta.TokenTracker?.RecordTimings(
                        chunk.Usage.PredictedTokens, chunk.Usage.PredictedMs, chunk.Usage.PredictedPerSecond);
                    turnTokens += chunk.Usage.TotalTokens;
                }

                if (chunk.IsComplete)
                    break;
            }
            }
            finally
            {
                if (!indicatorCts.IsCancellationRequested)
                    indicatorCts.Cancel();
                await indicatorTask;
                _output.ClearWaitingIndicator();
            }

            if (thinkingStarted && !thinkingCollapsed)
                _output.CollapseThinking(thinkingChars);

            _output.EndAssistantResponse(new TurnMetrics
            {
                PromptTokens = lastUsage?.PromptTokens ?? 0,
                CompletionTokens = lastUsage?.CompletionTokens ?? turnTokens,
                TimeToFirstToken = ttft,
                TotalElapsed = requestSw.Elapsed,
                GenTokensPerSecond = lastUsage?.PredictedPerSecond ?? 0,
                AvgGenTokensPerSecond = _session.Meta.TokenTracker?.AvgGenTokensPerSecond ?? 0,
            });

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
                await EmitUsageAsync();
                return;
            }

            if (_doomLoop.Check(toolCalls))
            {
                await siblingAbortCts.CancelAsync();
                const string doomMsg = "⚠ Doom loop detected: agent is repeating the same tool calls. Stopping.";
                _output.WriteWarning(doomMsg);
                if (_sink is not null) _ = _sink.OnSubAgentLogAsync(doomMsg);
                _session.AddMessage(new Message
                {
                    Role = MessageRole.User,
                    Content = "[System: Doom loop detected — you called the same tools 3 times in a row with identical arguments. Stop repeating and try a different approach, or ask the user for help.]",
                });
                _journal.FinishTurn("doom_loop");
                await EmitUsageAsync();
                return;
            }

            // Capture mode before tools run so an agent-initiated change (EnterPlanMode /
            // ExitPlanMode flips _session.Meta.PlanMode) can be detected and pushed to the
            // frontend below — the agent must never change mode without the UI/TUI learning.
            var planModeBeforeTools = _session.Meta.PlanMode;
            Log.Info($"[OMA_MODE_DETECT] Before tools: PlanMode={planModeBeforeTools}");

            var results = await ExecuteToolCallsWithInflightAsync(toolCalls, inFlightTasks, context, siblingAbortCts, ct);
            Log.Info($"[OMA_MODE_DETECT] ExecuteToolCallsWithInflightAsync returned normally");

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
                    IsError = result.IsError,
                });

                if (_sink is not null)
                {
                    var artifactId = result.Artifacts.Count > 0 ? result.Artifacts[0].Id : null;
                    await _sink.OnToolResultPreviewAsync(call.Id, result.ModelPreview, artifactId);
                }
            }

            // Agent-initiated mode change (EnterPlanMode / ExitPlanMode): keep both frontends
            // in sync. Push an SSE event to the extension UI and print to the TUI. Done here
            // (before the BreakTurn early-return) so ExitPlanMode's plan→build flip is covered.
            Log.Info($"[OMA_MODE_DETECT] After tools: PlanMode={_session.Meta.PlanMode}, Before={planModeBeforeTools}, _sink={(_sink != null ? "present" : "null")}");
            if (_session.Meta.PlanMode != planModeBeforeTools)
            {
                var modeStr = _session.Meta.PlanMode ? "plan" : "build";
                _output.WriteInfo(_session.Meta.PlanMode
                    ? "✓ Switched to Plan mode — only read-only tools are available"
                    : "✓ Switched to Build mode — all tools are available");
                Log.Info($"[OMA_MODE_DETECT] Mode changed! Calling OnModeChangedAsync('{modeStr}')");
                if (_sink is not null) await _sink.OnModeChangedAsync(modeStr);
                Log.Info($"[OMA_MODE] Agent changed mode mid-turn → {modeStr.ToUpperInvariant()}; notified frontend");
            }

            var pendingImages = results
                .Where(r => r.Images is { Count: > 0 })
                .SelectMany(r => r.Images!)
                .ToList();
            if (pendingImages.Count > 0)
                _session.AddMessage(new Message
                {
                    Role = MessageRole.User,
                    Content = $"[{pendingImages.Count} image(s) from tool calls]",
                    ContentParts = [new TextPart("Images retrieved by tools:"), .. pendingImages],
                });

            if (results.Any(r => r.BreakTurn))
            {
                if (_session.Meta.LastPlanContent is { Length: > 0 } planText)
                {
                    if (_sink is not null)
                    {
                        // Extension: render the plan + options as a card with buttons.
                        await _sink.OnPlanReadyAsync(planText, _session.Meta.LastPlanPath);
                    }
                    else
                    {
                        // TUI: show the plan + options; the user types 1/2/3 to choose.
                        _output.WriteMarkdown(planText);
                        _output.WriteInfo($"\n{ModeInstructions.ProceedOptions}\n\n(reply 1/2/3, or keep typing to refine the plan)");
                    }
                }
                _session.AddMessage(new Message
                {
                    Role = MessageRole.User,
                    Content = ModeInstructions.PlanPresented,
                });
                _journal.FinishTurn("turn_break");
                await EmitUsageAsync();
                return;
            }
        }

        await ReportIterationCapAsync(maxIterations, new List<ToolCall>(), ct);
        _journal.FinishTurn("max_iterations");
        await EmitUsageAsync();
        }
        finally
        {
            _liveFeedback?.EndTurn();
        }
    }

    private async Task ReportIterationCapAsync(int maxIterations, List<ToolCall> lastToolCalls, CancellationToken ct)
    {
        var toolCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var filesTouched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "FileRead", "FileEdit", "FileWrite", "Read", "Edit", "Write" };

        foreach (var msg in _session.Messages)
        {
            if (msg.ToolCalls is null) continue;
            foreach (var call in msg.ToolCalls)
            {
                toolCounts.TryGetValue(call.Name, out var n);
                toolCounts[call.Name] = n + 1;

                if (!fileToolNames.Contains(call.Name)) continue;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(call.Arguments);
                    foreach (var key in new[] { "file_path", "path" })
                    {
                        if (doc.RootElement.TryGetProperty(key, out var el)
                            && el.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            var p = el.GetString();
                            if (!string.IsNullOrWhiteSpace(p)) filesTouched.Add(p);
                        }
                    }
                }
                catch { }
            }
        }

        _output.WriteWarning($"Safety cap reached — the agent consumed all {maxIterations} steps allowed per turn.");
        _output.WriteWarning("This limit exists to prevent runaway tasks from running indefinitely.");
        _output.WriteInfo("Session breakdown:");

        if (toolCounts.Count > 0)
        {
            var toolSummary = string.Join("  ", toolCounts
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}×{kv.Value}"));
            _output.WriteInfo($"  Tools used:   {toolSummary}");
        }

        if (filesTouched.Count > 0)
            _output.WriteInfo($"  Files touched ({filesTouched.Count}): {string.Join(", ", filesTouched.OrderBy(f => f))}");

        if (lastToolCalls.Count > 0)
        {
            var lastArgs = lastToolCalls[0].Arguments;
            if (lastArgs.Length > 150) lastArgs = lastArgs[..150] + "…";
            _output.WriteInfo($"  Last action:  {lastToolCalls[0].Name} — {lastArgs}");
        }

        _output.WriteInfo("Summarising what was accomplished...");

        var convText = new System.Text.StringBuilder();
        foreach (var msg in _session.Messages)
        {
            if (msg.Role == MessageRole.System) continue;
            var role = msg.Role.ToString().ToUpperInvariant();
            convText.AppendLine($"[{role}]: {msg.Content ?? "(tool call/result)"}\n");
        }

        var summaryMessages = new List<Message>
        {
            new()
            {
                Role = MessageRole.System,
                Content = """
                    An AI coding agent was stopped after hitting its iteration cap. Summarise what it accomplished.
                    Use short bullet points only. Cover:
                    - What was completed
                    - What was partially done or in progress
                    - What was not started or left unresolved
                    - Any repeated errors or blockers the agent kept hitting

                    Do not call tools. Plain text only.
                    """,
            },
            new() { Role = MessageRole.User, Content = convText.ToString() },
        };

        try
        {
            var sb = new System.Text.StringBuilder();
            var opts = new LlmOptions { MaxTokens = 1024, Temperature = 0.1 };
            await foreach (var chunk in _llm.StreamChatAsync(summaryMessages, tools: null, opts, ct))
            {
                if (chunk.TextDelta is not null) sb.Append(chunk.TextDelta);
            }
            if (sb.Length > 0)
                _output.WriteInfo(sb.ToString());
        }
        catch (Exception ex)
        {
            _output.WriteDebug($"[IterationCap] Summary call failed: {ex.Message}");
        }

        _output.WriteInfo("To continue:");
        _output.WriteInfo("  Type your next message — the agent will pick up from context");
        _output.WriteInfo("  /compact    — summarise history to free context space before continuing");
        _output.WriteInfo("  /checkpoint — compress older turns and continue with a fresh window");
        _output.WriteInfo("  /clear      — wipe the session and start fresh");
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

    public async Task RunManualCompactionAsync(string? customInstructions, CancellationToken ct)
    {
        var lastPromptTokens = _session.Meta.TokenTracker?.LastPromptTokens ?? 0;
        await RunCompactionAsync(lastPromptTokens, customInstructions, ct);
    }

    private async Task RunCompactionAsync(int promptTokens, string? customInstructions, CancellationToken ct)
    {
        _output.WriteDebug($"[Compact] Triggered — messages={_session.Messages.Count} lastPromptTokens={promptTokens}");
        _session.Meta.IsCompacting = true;
        _output.ShowWaitingIndicator("Compacting");
        CompactionReport report;
        try
        {
            SessionState compacted;
            (compacted, report) = await _compactor.CompactAsync(_session, customInstructions, ct);

            _session.Messages.Clear();
            foreach (var msg in compacted.Messages)
                _session.AddMessage(msg);

            // Compaction re-summarises full raw history, so any prior checkpoint (which pointed
            // at an index into the now-discarded message list) is stale — drop it, or
            // BuildContextWindow would splice a checkpoint bubble that no longer lines up.
            _session.Checkpoints.Clear();
            _session.CheckpointCutoffIndex = 0;
        }
        finally
        {
            _session.Meta.IsCompacting = false;
            _output.ClearWaitingIndicator();
        }

        // Reflect the new (smaller) occupancy immediately, rather than leaving the pre-compaction
        // number on screen until the next real LLM response reports usage.
        _session.Meta.TokenTracker?.SetEstimatedPromptTokens(report.TokensAfter);
        await EmitUsageAsync();

        report.RenderTo(_output.WriteInfo, promptTokens);
        _output.WriteDebug($"[Compact] Done — {_session.Messages.Count} messages remaining");

        if (_sink is not null)
            await _sink.OnCompactionAsync(report.MessagesCompressed, report.Duration.TotalSeconds, _session.Checkpoints.Count);
    }

    private Task EmitUsageAsync()
    {
        if (_sink is null) return Task.CompletedTask;
        var tracker = _session.Meta.TokenTracker;
        if (tracker is null) return Task.CompletedTask;
        // context_tokens = LastPromptTokens (the full conversation sent on the most recent call =
        // current context occupancy); context_window = n_ctx (fetched from /props at startup).
        return _sink.OnUsageAsync(
            tracker.TotalPromptTokens,
            tracker.TotalCompletionTokens,
            tracker.TotalTokens,
            tracker.LastPromptTokens,
            _config.Llm.ContextSize,
            tracker.LastGenTokensPerSecond,
            tracker.AvgGenTokensPerSecond);
    }


    private async Task<List<ToolResult>> ExecuteToolCallsAsync(
        List<ToolCall> toolCalls,
        ToolContext context,
        CancellationToken ct)
    {
        var parallel = new List<(int Index, ToolCall Call, ITool Tool)>();
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

            if (tool.IsConcurrencySafe)
                parallel.Add((i, call, tool));
            else
                writeable.Add((i, call, tool));
        }

        var results = new ToolResult[toolCalls.Count];

        if (parallel.Count > 0)
        {
            await Task.WhenAll(parallel.Select(async item =>
            {
                var result = await _executor.ExecuteAsync(item.Call, item.Tool, context, ct);
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

            results[item.Index] = await _executor.ExecuteAsync(item.Call, item.Tool, context, ct);
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
        var parallelPending = new List<(int Index, ToolCall Call, ITool Tool)>();
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

            if (tool.IsConcurrencySafe)
            {

                if (!inFlightTasks.ContainsKey(call.Id))
                {
                    parallelPending.Add((i, call, tool));
                }
            }
            else
            {
                writeable.Add((i, call, tool));
            }
        }

        foreach (var item in parallelPending)
        {
            inFlightTasks[item.Call.Id] = Task.Run(
                () => _executor.ExecuteAsync(item.Call, item.Tool, context, siblingAbortCts.Token),
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
            catch (PendingUserResponseException)
            {
                // User interaction pending (permission, input, etc.) — propagate to turn runner to pause
                Log.Info($"[PENDING_RESPONSE] Tool {call.Name} paused for user response — re-throwing to turn runner");
                throw;
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

            try
            {
                results[item.Index] = await _executor.ExecuteAsync(item.Call, item.Tool, context, ct);
            }
            catch (PendingUserResponseException)
            {
                // User interaction pending (permission, input, etc.) — propagate to turn runner to pause
                Log.Info($"[PENDING_RESPONSE] Tool {item.Call.Name} paused for user response — re-throwing to turn runner");
                throw;
            }
        }

        return [.. results];
    }

    private ToolContext BuildToolContext() => new()
    {
        ToolRegistry = _tools,
        Session = _session,
        Permissions = _permissions,
        Config = _config,
        WorkingDirectory = _config.WorkingDirectory,
        WriteOutput = text =>
        {
            _output.WriteMarkdown(text);
            if (_sink is not null) _ = _sink.OnSubAgentLogAsync(text);
        },
        AskUser = (question, ct) => _input.AskUserAsync(question, ct),
        FileHistory = _session.Meta.FileHistory,
        Cursors = _cursorStore,
        BeginResponse = _output.StartAssistantResponse,
        EndResponse = () => _output.EndAssistantResponse(),
        StreamText = text =>
        {
            _output.StreamText(text);
            if (_sink is not null) _ = _sink.OnSubAgentLogAsync(text);
        },
        OnDebug = msg => { _output.WriteDebug(msg); Log.Debug(msg); },
        Output = _output,
        Interaction = _interaction,
        AgentDepth = _agentDepth,
    };
}
