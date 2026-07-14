using System.Text.Json;
using OpenMono.Session;

namespace OpenMono.Acp;





















public sealed class AcpTurnRunner : IAcpEventSink
{
    private readonly AcpSession _acpSession;
    private readonly SseWriter _writer;
    private readonly ConversationLoopFactory _loopFactory;
    private readonly AcpServerSettings _settings;
    private readonly IAcpUserInteraction _interaction;

    public AcpTurnRunner(
        AcpSession session,
        SseWriter writer,
        ConversationLoopFactory loopFactory,
        AcpServerSettings settings)
    {
        _acpSession = session;
        _writer = writer;
        _loopFactory = loopFactory;
        _settings = settings;
        _interaction = new AcpUserInteractionForwarder(session, writer, settings.PendingUserResponseTimeout);
    }



    public async Task RunUserMessageAsync(string userText, CancellationToken ct)
    {
        _acpSession.Messages.Add(new Message { Role = MessageRole.User, Content = userText });
        _acpSession.TurnCount++;
        await DriveLoopAsync(ct);
    }

    public async Task ResumeWithPermissionAsync(JsonElement payload, CancellationToken ct)
    {
        var id = payload.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("permission_response missing `id`");
        var decision = payload.TryGetProperty("decision", out var dEl) ? dEl.GetString() : null;
        var allow = string.Equals(decision, "allow", StringComparison.Ordinal);

        var ctx = _acpSession.LookupPauseContext(id)
            ?? throw new InvalidOperationException($"permission_response for unknown or already-resolved pause id: {id}");
        if (ctx.Kind != PendingResponseKind.Permission)
            throw new InvalidOperationException($"pause {id} is not a Permission pause (was {ctx.Kind})");

        if (!_acpSession.TryResolvePause(id, new AcpPermissionResponse(allow)))
            throw new InvalidOperationException($"failed to resolve pause id: {id}");




        // The decision is cached under the pause's context key; the conversation loop
        // re-executes the still-unanswered tool calls itself on the next drive — the
        // model is never asked to re-issue the call.
        _acpSession.RememberPermission(ctx.ContextKey, allow);

        await DriveLoopAsync(ct);
    }

    public async Task ResumeWithUserInputAsync(JsonElement payload, CancellationToken ct)
    {
        var id = payload.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("user_input_response missing `id`");
        var value = payload.TryGetProperty("value", out var vEl) ? vEl.GetString() ?? "" : "";

        var ctx = _acpSession.LookupPauseContext(id)
            ?? throw new InvalidOperationException($"user_input_response for unknown or already-resolved pause id: {id}");
        if (ctx.Kind != PendingResponseKind.UserInput)
            throw new InvalidOperationException($"pause {id} is not a UserInput pause (was {ctx.Kind})");

        if (!_acpSession.TryResolvePause(id, new AcpUserInputResponse(value)))
            throw new InvalidOperationException($"failed to resolve pause id: {id}");

        // Same as the permission path: the answer is cached, and re-running the paused
        // tool call consumes it via the interaction forwarder.
        _acpSession.RememberUserInput(ctx.ContextKey, value);

        await DriveLoopAsync(ct);
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









    private SessionState BuildSessionState()
    {
        var ss = new SessionState();
        foreach (var m in _acpSession.Messages) ss.AddMessage(m);
        ss.TurnCount = _acpSession.TurnCount;
        ss.Meta.PlanMode = _acpSession.PlanMode;
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
        _acpSession.Todos.Clear();
        foreach (var t in ss.Todos) _acpSession.Todos.Add(t);
    }



    public Task OnTextDeltaAsync(string content)
        => _writer.WriteEventAsync("text_delta", new { content });

    public Task OnThinkingDeltaAsync(string content)
        => _writer.WriteEventAsync("thinking_delta", new { content });

    public Task OnToolStartAsync(string callId, string name, string summary)
        => _writer.WriteEventAsync("tool_start", new { id = callId, name, summary });

    public Task OnToolEndAsync(string callId, string name, bool ok, double durationMs)
        => _writer.WriteEventAsync("tool_end", new { id = callId, name, ok, duration_ms = durationMs });

    public Task OnToolResultPreviewAsync(string callId, string preview, string? artifactId)
        => _writer.WriteEventAsync("tool_result_preview", new
        {
            id = callId,
            preview,
            artifact_id = artifactId,
        });

    public Task OnCompactionAsync(int messagesCompressed, double durationSeconds, int checkpointIndex)
        => _writer.WriteEventAsync("compaction", new
        {
            messages_compressed = messagesCompressed,
            duration_seconds = durationSeconds,
            checkpoint_index = checkpointIndex,
        });

    public Task OnUsageAsync(int inputTokens, int outputTokens, int totalTokens)
        => _writer.WriteEventAsync("usage", new
        {
            input_tokens = inputTokens,
            output_tokens = outputTokens,
            total_tokens = totalTokens,
        });
}
