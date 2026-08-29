namespace OpenMono.Session;

/// <summary>
/// Rough char-count → token estimator (chars/4) shared by compaction, checkpointing and the
/// live context-usage heartbeat so every consumer measures occupancy the same way instead of
/// each re-implementing the counting.
/// </summary>
public static class TokenEstimator
{
    public static int EstimateMessages(IReadOnlyList<Message> messages)
        => messages.Sum(MessageCharCount) / 4;

    public static int EstimateMessage(Message message) => MessageCharCount(message) / 4;

    public static int EstimateForChars(int chars) => chars / 4;

    private static int MessageCharCount(Message m)
        => (m.Content?.Length ?? 0)
           + (m.ToolCalls?.Sum(c => c.Arguments?.Length ?? 0) ?? 0)
           + 20;
}