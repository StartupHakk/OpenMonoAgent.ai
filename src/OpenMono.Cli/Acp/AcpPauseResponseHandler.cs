using System.Text.Json;
using OpenMono.Session;
using OpenMono.Utils;

namespace OpenMono.Acp;

/// <summary>
/// Unified handler for all pause responses (permission, user_input, playbook_approval, etc.)
///
/// This replaces the scattered ResumeWithPermissionAsync, ResumeWithUserInputAsync,
/// ResumeWithPlaybookApprovalAsync methods by centralizing the pause response logic.
///
/// The key pattern:
/// 1. Resolve the pause (unblock the awaiting agent code)
/// 2. Execute pending operations (tool execution, etc.)
/// 3. Continue the turn (get model's next response)
/// 4. Stream all events back via SSE
/// </summary>
public class AcpPauseResponseHandler
{
    private readonly AcpSession _session;
    private readonly SseWriter _writer;
    private readonly ConversationLoopFactory _loopFactory;
    private readonly IAcpEventSink _eventSink;
    private readonly IAcpUserInteraction _interaction;

    public AcpPauseResponseHandler(
        AcpSession session,
        SseWriter writer,
        ConversationLoopFactory loopFactory,
        IAcpEventSink eventSink,
        IAcpUserInteraction interaction)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _loopFactory = loopFactory ?? throw new ArgumentNullException(nameof(loopFactory));
        _eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
        _interaction = interaction ?? throw new ArgumentNullException(nameof(interaction));
    }

    /// <summary>
    /// Handle any pause response and continue the turn.
    ///
    /// This is called when the client sends back a response to a pause (permission, user_input, etc.)
    /// </summary>
    public async Task HandlePauseResponseAsync(
        JsonElement payload,
        CancellationToken ct)
    {
        if (payload.ValueKind == JsonValueKind.Undefined)
            throw new ArgumentException("Pause response payload is undefined", nameof(payload));

        var pauseId = payload.TryGetProperty("id", out var idEl)
            ? idEl.GetString()
            : throw new InvalidOperationException("Pause response missing 'id'");

        if (string.IsNullOrEmpty(pauseId))
            throw new InvalidOperationException("Pause response id cannot be empty");

        // 1. Look up the pause to understand its type
        var pauseCtx = _session.LookupPauseContext(pauseId);
        if (!pauseCtx.HasValue)
            throw new InvalidOperationException($"Unknown pause: id={pauseId}");

        var pauseKind = pauseCtx.Value.Kind;
        Log.Info($"[PAUSE_HANDLER] Processing {pauseKind} pause response: id={pauseId}");

        // 2. Parse the response based on pause type
        var response = ParseResponse(pauseKind, payload);

        // 3. Resolve the pause (unblocks the awaiting agent code)
        if (!_session.TryResolvePause(pauseId, response))
            throw new InvalidOperationException($"Failed to resolve pause: id={pauseId}");

        Log.Info($"[PAUSE_HANDLER] Pause resolved: id={pauseId}");

        // 4. Handle pause-type-specific logic (scope caching, etc.)
        HandlePauseLogic(pauseKind, pauseCtx.Value.ContextKey, response, payload);

        // 5. Build fresh session state and continue the turn
        var sessionState = BuildSessionState();
        using var loop = _loopFactory.Create(sessionState, sink: _eventSink, interaction: _interaction);

        try
        {
            // If this was a permission or playbook pause, execute the pending tool
            if (pauseKind == PendingResponseKind.Permission ||
                pauseKind == PendingResponseKind.PlaybookApproval)
            {
                var permResponse = response as AcpPermissionResponse;
                if (permResponse == null)
                    throw new InvalidOperationException($"Expected PermissionResponse for {pauseKind}");

                Log.Info($"[PAUSE_HANDLER] Executing tool with decision={permResponse.Allow}");
                await loop.ResolvePendingToolCallsAsync(permResponse.Allow, ct);
            }
            // For user_input and other types, no special execution—just continue the turn

            // 6. Continue the turn: LLM processes tool results and generates response
            Log.Info($"[PAUSE_HANDLER] Continuing turn after {pauseKind} resolution");
            await loop.ContinueTurnAsync(ct);

            // 7. Sync changes back to AcpSession
            SyncBackToAcpSession(sessionState);

            // 8. Signal completion
            await _writer.WriteEventAsync("done", new { });
            Log.Info($"[PAUSE_HANDLER] Turn completed successfully");
        }
        catch (PendingUserResponseException pureEx)
        {
            // Another pause was triggered during turn continuation
            // The new pause event has already been emitted by the interaction handler
            SyncBackToAcpSession(sessionState);
            Log.Info($"[PAUSE_HANDLER] Another pause triggered: id={pureEx.PauseId} kind={pureEx.Kind}");
            // Don't close the SSE stream; let it continue for the next pause
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SyncBackToAcpSession(sessionState);
            Log.Warn($"[PAUSE_HANDLER] Turn cancelled");
            throw;
        }
        catch (Exception ex)
        {
            SyncBackToAcpSession(sessionState);
            Log.Error($"[PAUSE_HANDLER] Error continuing turn: {ex.Message}");
            await _writer.WriteEventAsync("error", new { message = ex.Message });
            throw;
        }
    }

    /// <summary>
    /// Parse a pause response JSON into the appropriate response object.
    /// </summary>
    private AcpPauseResponse ParseResponse(PendingResponseKind kind, JsonElement payload)
    {
        return kind switch
        {
            PendingResponseKind.Permission =>
                new AcpPermissionResponse(
                    payload.TryGetProperty("decision", out var d) && d.GetString() == "allow"),

            PendingResponseKind.UserInput =>
                new AcpUserInputResponse(
                    payload.TryGetProperty("value", out var v) && v.ValueKind != JsonValueKind.Null
                        ? v.GetString() ?? ""
                        : ""),

            PendingResponseKind.PlaybookApproval =>
                new AcpPermissionResponse(
                    payload.TryGetProperty("decision", out var d) && d.GetString() == "allow"),

            _ => throw new InvalidOperationException($"Unsupported pause kind: {kind}"),
        };
    }

    /// <summary>
    /// Handle pause-type-specific logic like caching decisions.
    /// </summary>
    private void HandlePauseLogic(
        PendingResponseKind kind,
        string contextKey,
        AcpPauseResponse response,
        JsonElement originalPayload)
    {
        if (kind != PendingResponseKind.Permission)
            return;

        if (response is not AcpPermissionResponse permResponse)
            return;

        // Extract scope from original payload (default to "once")
        var scope = originalPayload.TryGetProperty("scope", out var scopeEl)
            ? scopeEl.GetString()
            : "once";

        // Scope-aware caching
        // "session" → cache for entire session (one approval covers all invocations)
        // "once" → temporary grant (only for this invocation, then forget)
        var isCachingForSession = string.Equals(scope, "session", StringComparison.Ordinal);

        if (isCachingForSession)
        {
            _session.RememberPermission(contextKey, permResponse.Allow);
            Log.Info($"[PAUSE_HANDLER] Cached permission for session: contextKey={contextKey} allow={permResponse.Allow}");
        }
        else if (permResponse.Allow)
        {
            // Temporary grant for this execution only
            _session.RememberPermission(contextKey, true);
            Log.Info($"[PAUSE_HANDLER] Seeded temporary permission for once: contextKey={contextKey}");
        }
    }

    /// <summary>
    /// Build a fresh SessionState from the current AcpSession.
    /// Used to create a new conversation loop context for continuing the turn.
    /// </summary>
    private SessionState BuildSessionState()
    {
        var ss = new SessionState();
        foreach (var m in _session.Messages) ss.AddMessage(m);
        ss.TurnCount = _session.TurnCount;
        ss.Meta.PlanMode = _session.PlanMode;
        ss.Meta.AutoApproveWrites = _session.AutoApproveWrites;
        ss.Todos.Clear();
        foreach (var t in _session.Todos) ss.Todos.Add(t);
        ss.Meta.TokenTracker ??= new TokenTracker();
        return ss;
    }

    /// <summary>
    /// Sync changes from SessionState back to AcpSession.
    /// Called after turn continuation to persist any updates.
    /// </summary>
    private void SyncBackToAcpSession(SessionState ss)
    {
        _session.Messages.Clear();
        _session.Messages.AddRange(ss.Messages);
        _session.PlanMode = ss.Meta.PlanMode;
        _session.AutoApproveWrites = ss.Meta.AutoApproveWrites;
        _session.Todos.Clear();
        foreach (var t in ss.Todos) _session.Todos.Add(t);
    }
}

