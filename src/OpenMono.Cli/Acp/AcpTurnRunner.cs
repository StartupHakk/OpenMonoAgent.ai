using System.Text.Json;
using OpenMono.Config;
using OpenMono.Memory;
using OpenMono.Playbooks;
using OpenMono.Session;
using OpenMono.Utils;

namespace OpenMono.Acp;

internal record SystemPromptContext(
    AppConfig Config,
    PlaybookRegistry? PlaybookRegistry = null,
    MemoryStore? MemoryStore = null,
    string? CachedPrompt = null);





















public sealed class AcpTurnRunner : IAcpEventSink
{
    private readonly AcpSession _acpSession;
    private readonly SseWriter _writer;
    private readonly ConversationLoopFactory _loopFactory;
    private readonly AcpServerSettings _settings;
    private readonly IAcpUserInteraction _interaction;
    private readonly PlaybookRegistry? _playbookRegistry;
    private readonly MemoryStore? _memoryStore;
    private string? _cachedSystemPrompt;

    public AcpTurnRunner(
        AcpSession session,
        SseWriter writer,
        ConversationLoopFactory loopFactory,
        AcpServerSettings settings,
        PlaybookRegistry? playbookRegistry = null,
        MemoryStore? memoryStore = null)
    {
        _acpSession = session;
        _writer = writer;
        _loopFactory = loopFactory;
        _settings = settings;
        _playbookRegistry = playbookRegistry;
        _memoryStore = memoryStore;
        _interaction = new AcpUserInteractionForwarder(session, writer, settings.PendingUserResponseTimeout);

        // Log system prompt availability on first turn for this session
        if (session.TurnCount == 0)
        {
            Log.Info($"[OMA_INIT] ACP session {session.Id} initialized. PlaybookRegistry available: {(playbookRegistry?.All.Count ?? 0)} playbooks. MemoryStore available: {(memoryStore is not null)}");
        }
    }



    public async Task RunUserMessageAsync(string userText, CancellationToken ct)
    {
        // Ensure system prompt is set on first message
        if (_acpSession.Messages.Count == 0 || _acpSession.Messages[0].Role != MessageRole.System)
        {
            // Build the full system prompt with playbooks, memory, and git context
            if (_cachedSystemPrompt == null)
            {
                Log.Info($"[OMA_SYSTEMPROMPT] Building full system prompt for session {_acpSession.Id}");
                _cachedSystemPrompt = await SystemPrompt.BuildAsync(
                    _loopFactory.Config,
                    _memoryStore,
                    _playbookRegistry);
                Log.Info($"[OMA_SYSTEMPROMPT] System prompt built: {_cachedSystemPrompt.Length} chars, {(_playbookRegistry?.All.Count ?? 0)} playbooks included");
            }

            Log.Info($"[OMA_SYSTEMPROMPT] Session {_acpSession.Id}: Adding system prompt ({_cachedSystemPrompt.Length} chars). Messages before: {_acpSession.Messages.Count}");
            _acpSession.Messages.Insert(0, new Message
            {
                Role = MessageRole.System,
                Content = _cachedSystemPrompt
            });
            Log.Info($"[OMA_SYSTEMPROMPT] System prompt added. Messages after: {_acpSession.Messages.Count}. First message is System: {_acpSession.Messages[0].Role == MessageRole.System}");
        }
        else
        {
            Log.Info($"[OMA_SYSTEMPROMPT] System prompt already present in message history, not adding again");
        }

        // Transform relative @ file references to absolute paths (e.g., @file.md → @/workspace/file.md)
        var transformedText = FileReferenceResolver.TransformRelativeReferences(userText, _loopFactory.Config.WorkingDirectory);

        _acpSession.Messages.Add(new Message { Role = MessageRole.User, Content = transformedText });
        _acpSession.TurnCount++;
        Log.Info($"[OMA_TURN] Session {_acpSession.Id} turn {_acpSession.TurnCount}: Processing message with {_acpSession.Messages.Count} total messages (first is System: {_acpSession.Messages[0].Role == MessageRole.System})");
        await DriveLoopAsync(ct);
    }

    public async Task ResumeWithPermissionAsync(JsonElement payload, CancellationToken ct)
    {
        var id = payload.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("permission_response missing `id`");
        var decision = payload.TryGetProperty("decision", out var dEl) ? dEl.GetString() : null;
        var allow = string.Equals(decision, "allow", StringComparison.Ordinal);

        // SCOPE-AWARE PERMISSION HANDLING (Phase 1 Implementation)
        // ─────────────────────────────────────────────────────────
        // scope: "session" → cache the decision for the entire session
        //   - Tool will not be re-prompted for this type/capability in this session
        //   - Stored in _acpSession.RememberPermission() (session-level cache)
        //
        // scope: "once" (default) → decision applies to only this invocation
        //   - No cache write; per-turn temporary scope
        //   - Future: consider per-turn denial tracking to prevent re-prompting same denied tool
        //
        // Security: Default to "once" scope if not specified. Extension must explicitly
        // choose "session" to get session-wide caching behavior.
        var scope = payload.TryGetProperty("scope", out var sEl) ? sEl.GetString() : "once";

        var ctx = _acpSession.LookupPauseContext(id)
            ?? throw new InvalidOperationException($"permission_response for unknown or already-resolved pause id: {id}");
        if (ctx.Kind != PendingResponseKind.Permission)
            throw new InvalidOperationException($"pause {id} is not a Permission pause (was {ctx.Kind})");

        if (!_acpSession.TryResolvePause(id, new AcpPermissionResponse(allow)))
            throw new InvalidOperationException($"failed to resolve pause id: {id}");

        // === Scope handling ===
        // "session" → cache the decision so this tool is not re-prompted this session.
        // "once"    → for an allow, seed a TEMPORARY grant so the resumed execution does
        //             not re-prompt, then forget it (below) so a later call prompts again.
        var isCaching = string.Equals(scope, "session", StringComparison.Ordinal);
        if (isCaching)
            _acpSession.RememberPermission(ctx.ContextKey, allow);
        else if (allow)
            _acpSession.RememberPermission(ctx.ContextKey, true);

        Log.Info($"[OMA_PERM] Resolved: id={id} decision={decision} scope={scope} caching={isCaching} contextKey={ctx.ContextKey}");

        // Execute-on-resume: actually run (or, if denied, refuse) the pending tool call and
        // feed the REAL result back to the model. This replaces the old "re-issue the tool
        // call" handshake, which never executed the tool (file unwritten) and let the model
        // hallucinate success from a bare "permission granted" message.
        var sessionState = BuildSessionState();
        using var loop = _loopFactory.Create(sessionState, sink: this, interaction: _interaction);
        try
        {
            try
            {
                await loop.ResolvePendingToolCallsAsync(allow, ct);
            }
            finally
            {
                // Strict "once": the temporary grant only ever covers the resumed execution.
                if (allow && !isCaching)
                    _acpSession.ForgetPermission(ctx.ContextKey);
            }

            await loop.ContinueTurnAsync(ct);
            SyncBackToAcpSession(sessionState);
            await _writer.WriteEventAsync("done", new { });
        }
        catch (PendingUserResponseException)
        {
            SyncBackToAcpSession(sessionState);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SyncBackToAcpSession(sessionState);
        }
        catch (Exception e)
        {
            SyncBackToAcpSession(sessionState);
            await _writer.WriteEventAsync("error", new { message = e.Message });
        }
    }

    public async Task ResumeWithUserInputAsync(JsonElement payload, CancellationToken ct)
    {
        var id = payload.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("user_input_response missing `id`");
        var value = payload.TryGetProperty("value", out var vEl) ? vEl.GetString() : null;

        var ctx = _acpSession.LookupPauseContext(id)
            ?? throw new InvalidOperationException($"user_input_response for unknown or already-resolved pause id: {id}");
        if (ctx.Kind != PendingResponseKind.UserInput)
            throw new InvalidOperationException($"pause {id} is not a UserInput pause (was {ctx.Kind})");

        var resolvedValue = value ?? "";
        if (!_acpSession.TryResolvePause(id, new AcpUserInputResponse(resolvedValue)))
            throw new InvalidOperationException($"failed to resolve pause id: {id}");

        _acpSession.RememberUserInput(ctx.ContextKey, resolvedValue);

        AppendSyntheticToolMessages(resolvedValue);

        await DriveLoopAsync(ct);
    }

    public async Task ResumeWithPlanDecisionAsync(string decision, CancellationToken ct)
    {
        var (implement, autoApprove, instruction) = ModeInstructions.ResolvePlanDecision(decision);

        if (!implement)
        {
            // "Keep planning" — stay in Plan mode; the user will refine via a normal message.
            Log.Info($"[OMA_MODE] plan_decision='{decision}' → keep planning (no change)");
            await _writer.WriteEventAsync("done", new { });
            return;
        }

        // Deterministic implement: flip to Build, set gating, tell the UI, then drive the turn.
        _acpSession.PlanMode = false;
        _acpSession.AutoApproveWrites = autoApprove;
        await OnModeChangedAsync("build");
        Log.Info($"[OMA_MODE] plan_decision='{decision}' → BUILD, autoApproveWrites={autoApprove}");

        _acpSession.Messages.Add(new Message { Role = MessageRole.User, Content = instruction });
        _acpSession.TurnCount++;
        await DriveLoopAsync(ct);
    }

    public async Task ResumeWithToggleModeAsync(JsonElement payload, CancellationToken ct)
    {
        var id = payload.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("toggle_mode_response missing `id`");
        var decision = payload.TryGetProperty("decision", out var dEl) ? dEl.GetString() : null;
        var allow = string.Equals(decision, "approve", StringComparison.Ordinal);

        var ctx = _acpSession.LookupPauseContext(id)
            ?? throw new InvalidOperationException($"toggle_mode_response for unknown or already-resolved pause id: {id}");
        if (ctx.Kind != PendingResponseKind.ToggleMode)
            throw new InvalidOperationException($"pause {id} is not a ToggleMode pause (was {ctx.Kind})");

        if (!_acpSession.TryResolvePause(id, new AcpPermissionResponse(allow)))
            throw new InvalidOperationException($"failed to resolve pause id: {id}");

        // Remember the decision so the tool doesn't re-prompt if resumed
        _acpSession.RememberPermission(ctx.ContextKey, allow);

        if (!allow)
        {
            // User declined to switch mode
            Log.Info($"[OMA_MODE] toggle_mode_request denied by user");
            await _writer.WriteEventAsync("done", new { });
            return;
        }

        // User approved mode switch: flip to Build and re-execute the pending tool call
        _acpSession.PlanMode = false;
        await OnModeChangedAsync("build");
        Log.Info($"[OMA_MODE] User approved mode switch to BUILD for playbook");

        var sessionState = BuildSessionState();
        using var loop = _loopFactory.Create(sessionState, sink: this, interaction: _interaction);
        try
        {
            await loop.ResolvePendingToolCallsAsync(true, ct);
            await loop.ContinueTurnAsync(ct);
            SyncBackToAcpSession(sessionState);
            await _writer.WriteEventAsync("done", new { });
        }
        catch (PendingUserResponseException)
        {
            SyncBackToAcpSession(sessionState);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SyncBackToAcpSession(sessionState);
        }
        catch (Exception e)
        {
            SyncBackToAcpSession(sessionState);
            await _writer.WriteEventAsync("error", new { message = e.Message });
        }
    }

    public void AbortPendingPauses()
    {
        _acpSession.CancelAllPending();
    }



    private async Task DriveLoopAsync(CancellationToken ct)
    {
        var sessionState = BuildSessionState();
        using var loop = _loopFactory.Create(sessionState, sink: this, interaction: _interaction);

        try
        {



            await loop.ContinueTurnAsync(ct);
            SyncBackToAcpSession(sessionState);
            await _writer.WriteEventAsync("done", new { });
        }
        catch (PendingUserResponseException)
        {



            SyncBackToAcpSession(sessionState);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {

            SyncBackToAcpSession(sessionState);
        }
        catch (Exception e)
        {
            SyncBackToAcpSession(sessionState);
            await _writer.WriteEventAsync("error", new { message = e.Message });
        }
    }









    private void AppendSyntheticToolMessages(string resolutionContent)
    {
        var lastAssistant = _acpSession.Messages
            .LastOrDefault(m => m.Role == MessageRole.Assistant && m.ToolCalls is not null);
        if (lastAssistant?.ToolCalls is null || lastAssistant.ToolCalls.Count == 0)
        {
            // If no assistant message with tool calls exists, we're likely resuming from a permission
            // pause before the LLM response was added to history. In this case, create a synthetic
            // assistant message with a generic tool call, then answer it with the resolution.
            Log.Info($"[OMA_SYNTHETIC] No pending tool calls found in history, but permission was resolved. Creating synthetic response to guide LLM.");
            var syntheticCallId = $"synthetic_{Guid.NewGuid().ToString("N")[..12]}";
            _acpSession.Messages.Add(new Message
            {
                Role = MessageRole.Assistant,
                Content = "I'll attempt the operation now that permission has been granted.",
                ToolCalls = [new ToolCall { Id = syntheticCallId, Name = "PendingTool", Arguments = "{}" }]
            });
            _acpSession.Messages.Add(new Message
            {
                Role = MessageRole.Tool,
                ToolCallId = syntheticCallId,
                ToolName = "PendingTool",
                Content = resolutionContent,
            });
            return;
        }

        var alreadyAnswered = _acpSession.Messages
            .Where(m => m.Role == MessageRole.Tool && m.ToolCallId is not null)
            .Select(m => m.ToolCallId!)
            .ToHashSet();

        var first = true;
        foreach (var call in lastAssistant.ToolCalls)
        {
            if (alreadyAnswered.Contains(call.Id)) continue;
            _acpSession.Messages.Add(new Message
            {
                Role = MessageRole.Tool,
                ToolCallId = call.Id,
                ToolName = call.Name,
                Content = first ? resolutionContent : "Execution deferred. Retry to run.",
            });
            first = false;
        }
    }

    private SessionState BuildSessionState()
    {
        var ss = new SessionState();
        foreach (var m in _acpSession.Messages) ss.AddMessage(m);
        ss.TurnCount = _acpSession.TurnCount;
        ss.Meta.PlanMode = _acpSession.PlanMode;
        ss.Meta.AutoApproveWrites = _acpSession.AutoApproveWrites;
        ss.Todos.Clear();
        foreach (var t in _acpSession.Todos) ss.Todos.Add(t);
        ss.Meta.TokenTracker ??= new TokenTracker();
        return ss;
    }

    private void SyncBackToAcpSession(SessionState ss)
    {
        _acpSession.Messages.Clear();
        _acpSession.Messages.AddRange(ss.Messages);
        _acpSession.PlanMode = ss.Meta.PlanMode;
        _acpSession.AutoApproveWrites = ss.Meta.AutoApproveWrites;
        _acpSession.Todos.Clear();
        foreach (var t in ss.Todos) _acpSession.Todos.Add(t);
    }



    public Task OnTextDeltaAsync(string content)
        => _writer.WriteEventAsync("text_delta", new { content });

    public Task OnThinkingDeltaAsync(string content)
        => _writer.WriteEventAsync("thinking_delta", new { content });

    public Task OnToolStartAsync(string callId, string name, string summary, string? arguments = null)
    {
        var payload = new { id = callId, name, summary, arguments };
        if (!string.IsNullOrEmpty(arguments))
            Log.Debug($"[ACP] OnToolStartAsync: {name} with arguments ({arguments.Length} bytes)");
        return _writer.WriteEventAsync("tool_start", payload);
    }

    public Task OnToolStatusAsync(string callId, string status)
        => _writer.WriteEventAsync("tool_status", new { id = callId, status });

    public Task OnToolEndAsync(string callId, string name, bool ok, double durationMs)
        => _writer.WriteEventAsync("tool_end", new { id = callId, name, ok, duration_ms = durationMs });

    public Task OnToolResultPreviewAsync(string callId, string preview, string? artifactId)
        => _writer.WriteEventAsync("tool_result_preview", new
        {
            id = callId,
            preview,
            artifact_id = artifactId,
        });

    public Task OnModeChangedAsync(string mode)
    {
        // Keep the ACP session (source of truth) consistent immediately, then notify the UI.
        _acpSession.PlanMode = string.Equals(mode, "plan", StringComparison.OrdinalIgnoreCase);
        return _writer.WriteEventAsync("mode_changed", new { mode });
    }

    public Task OnPlanReadyAsync(string planContent, string? planPath)
        => _writer.WriteEventAsync("plan_ready", new { plan = planContent, plan_path = planPath });

    public Task OnCompactionAsync(int messagesCompressed, double durationSeconds, int checkpointIndex)
        => _writer.WriteEventAsync("compaction", new
        {
            messages_compressed = messagesCompressed,
            duration_seconds = durationSeconds,
            checkpoint_index = checkpointIndex,
        });

    public Task OnUsageAsync(int inputTokens, int outputTokens, int totalTokens, int contextTokens, int contextWindow, double genTps, double avgTps)
        => _writer.WriteEventAsync("usage", new
        {
            input_tokens = inputTokens,
            output_tokens = outputTokens,
            total_tokens = totalTokens,
            context_tokens = contextTokens,
            context_window = contextWindow,
            gen_tps = genTps,
            avg_tps = avgTps,
        });

    public Task OnSubAgentLogAsync(string line)
        => _writer.WriteEventAsync("sub_agent_log", new { line });
}
