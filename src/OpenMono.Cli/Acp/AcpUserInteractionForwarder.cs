namespace OpenMono.Acp;













public sealed class AcpUserInteractionForwarder : IAcpUserInteraction
{
    private readonly AcpSession _session;
    private readonly SseWriter _writer;
    private readonly TimeSpan _timeout;

    public AcpUserInteractionForwarder(AcpSession session, SseWriter writer, TimeSpan timeout)
    {
        _session = session;
        _writer = writer;
        _timeout = timeout;
    }

    public async Task<bool> RequestPermissionAsync(string toolName, string summary, bool dangerous, CancellationToken ct)
    {
        var contextKey = PermissionContextKey(toolName, summary);

        if (_session.TryGetRememberedPermission(contextKey) is bool cached)
            return cached;

        // If a pause for this contextKey is already pending (e.g. from a concurrent
        // speculative tool execution), reuse that pause instead of registering a new
        // one. This prevents multiple concurrent calls from each writing their own
        // permission_request SSE event, which would leave stranded pauses that show
        // up as "crashed: Awaiting client Permission response" in the history.
        foreach (var existingId in _session.PendingIds)
        {
            var ctx = _session.LookupPauseContext(existingId);
            if (ctx?.Kind == PendingResponseKind.Permission && ctx?.ContextKey == contextKey)
                throw new PendingUserResponseException(existingId, PendingResponseKind.Permission);
        }

        var id = "perm_" + Guid.NewGuid().ToString("N")[..12];
        _session.RegisterPause(id, PendingResponseKind.Permission, contextKey);

        await _writer.WriteEventAsync("permission_request", new
        {
            id,
            tool = toolName,
            summary,
            dangerous,
        });

        throw new PendingUserResponseException(id, PendingResponseKind.Permission);
    }

    public async Task<bool> RequestToggleModeAsync(string reason, CancellationToken ct)
    {
        var contextKey = "toggle_mode:" + reason;

        if (_session.TryGetRememberedPermission(contextKey) is bool cached)
            return cached;

        var id = "mode_" + Guid.NewGuid().ToString("N")[..12];
        _session.RegisterPause(id, PendingResponseKind.ToggleMode, contextKey);

        await _writer.WriteEventAsync("toggle_mode_request", new
        {
            id,
            reason,
        });

        throw new PendingUserResponseException(id, PendingResponseKind.ToggleMode);
    }

    public async Task<string?> RequestUserInputAsync(string question, CancellationToken ct)
    {



        if (_session.TryGetRememberedUserInput(question) is { } cached)
            return cached;

        var id = "ask_" + Guid.NewGuid().ToString("N")[..12];
        _session.RegisterPause(id, PendingResponseKind.UserInput, question);

        await _writer.WriteEventAsync("user_input_request", new
        {
            id,
            question,
        });

        throw new PendingUserResponseException(id, PendingResponseKind.UserInput);
    }


    // Cache by tool name only so one Allow covers all invocations of a tool in a
    // session. Caching by {toolName}|{summary} required a separate dialog for every
    // distinct query/URL, producing 7+ prompts for a single web-research turn.
    // Dangerous tools still show a warning in the UI; they just don't re-prompt
    // if the user already approved that tool earlier in the session.
    public static string PermissionContextKey(string toolName, string summary)
        => toolName;
}
