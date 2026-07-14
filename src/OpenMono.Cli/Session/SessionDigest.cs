namespace OpenMono.Session;

public static class SessionDigest
{
    public static string DeriveTitle(IReadOnlyList<Message> messages, int maxLength = 80)
    {
        var first = messages.FirstOrDefault(m => m.Role == MessageRole.User)?.Content;
        if (string.IsNullOrWhiteSpace(first)) return "";

        var collapsed = string.Join(' ', first.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length <= maxLength) return collapsed;
        return collapsed[..maxLength] + "…";
    }

    public static string? DeriveLatestSummary(IReadOnlyList<CheckpointEntry> checkpoints)
        => checkpoints.Count > 0 ? checkpoints[^1].Summary : null;
}
