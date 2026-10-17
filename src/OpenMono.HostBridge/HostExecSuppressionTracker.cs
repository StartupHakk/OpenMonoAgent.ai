namespace OpenMono.HostBridge;

/// <summary>
/// Pairs in-container <c>AskUser</c> tool calls with the
/// <c>user_input_request</c> pauses they produce, so HOST_EXEC carriers can be
/// suppressed exactly (no free-text parsing).
///
/// The container always emits <c>tool_start(AskUser, id=X)</c> before call X's
/// prompt arrives, so FIFO pairing is exact. Genuine questions are never
/// marked — the caller only suppresses when the paired question carries a
/// HOST_EXEC envelope.
/// </summary>
internal sealed class HostExecSuppressionTracker
{
    private const int MaxPending = 64;
    private readonly Queue<string> _pending = new();
    private readonly HashSet<string> _suppressed = new(StringComparer.Ordinal);

    /// <summary>Record an AskUser tool start. Cards render on classification, not here.</summary>
    public void TrackAskUserStart(string callId)
    {
        if (string.IsNullOrEmpty(callId))
            return;
        if (_pending.Count > MaxPending)
            _pending.Clear();
        _pending.Enqueue(callId);
    }

    /// <summary>
    /// Pair the next prompt with its tool call. Returns the linked call id (or
    /// null when the prompt had no AskUser origin). When
    /// <paramref name="isHostExec"/> is true the linked call is marked
    /// suppressed (its card and tool_end are swallowed).
    /// </summary>
    public string? LinkPrompt(bool isHostExec)
    {
        if (_pending.Count == 0)
            return null;
        var callId = _pending.Dequeue();
        if (isHostExec)
        {
            if (_suppressed.Count > MaxPending)
                _suppressed.Clear();
            _suppressed.Add(callId);
        }
        return callId;
    }

    /// <summary>True when this tool_end belongs to a suppressed HOST_EXEC carrier.</summary>
    public bool ConsumeIfSuppressed(string callId) =>
        !string.IsNullOrEmpty(callId) && _suppressed.Remove(callId);

    public void Clear()
    {
        _pending.Clear();
        _suppressed.Clear();
    }
}
