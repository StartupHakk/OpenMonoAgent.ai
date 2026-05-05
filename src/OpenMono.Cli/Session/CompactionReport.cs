namespace OpenMono.Session;

public sealed record CompactionReport
{
    public required int MessagesBefore { get; init; }
    public required int MessagesAfter { get; init; }
    public required int MessagesCompressed { get; init; }
    public required int TokensBefore { get; init; }
    public required int TokensAfter { get; init; }
    public required Dictionary<MessageRole, int> CompressedByRole { get; init; }
    public required Dictionary<string, int> CompressedToolCalls { get; init; }
    public required List<string> FilesTouched { get; init; }
    public required int ToolOutputsEvicted { get; init; }
    public required int EvictedBytes { get; init; }
    public required TimeSpan Duration { get; init; }
    public required int ContextWindowSize { get; init; }

    public void RenderTo(Action<string> writeInfo, int promptTokensBefore = 0)
    {
        const string sep = "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━";
        var ctxPct = ContextWindowSize > 0 && promptTokensBefore > 0
            ? promptTokensBefore * 100 / ContextWindowSize
            : 0;

        writeInfo(sep);
        writeInfo(ctxPct > 0
            ? $"🗜  Running compaction — context at {ctxPct}% of window"
            : "🗜  Running compaction");
        writeInfo(sep);

        if (MessagesCompressed == 0)
        {
            writeInfo("Nothing to compact — conversation too short or already compact.");
            writeInfo(sep);
            return;
        }

        writeInfo($"Compressing {MessagesCompressed} messages → structured summary");

        if (CompressedByRole.Count > 0)
        {
            var roles = string.Join(", ", CompressedByRole
                .OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Value} {kv.Key.ToString().ToLowerInvariant()}"));
            writeInfo($"   • {roles}");
        }

        if (CompressedToolCalls.Count > 0)
        {
            var tools = string.Join(", ", CompressedToolCalls
                .OrderByDescending(kv => kv.Value)
                .Take(8)
                .Select(kv => $"{kv.Key}×{kv.Value}"));
            writeInfo($"   • Tool calls: {tools}");
        }

        if (FilesTouched.Count > 0)
        {
            var files = FilesTouched.Count <= 5
                ? string.Join(", ", FilesTouched)
                : string.Join(", ", FilesTouched.Take(5)) + $" (+{FilesTouched.Count - 5} more)";
            writeInfo($"   • Files touched: {files}");
        }

        if (ToolOutputsEvicted > 0)
        {
            var kb = EvictedBytes / 1024;
            writeInfo($"   • Evicted {ToolOutputsEvicted} large tool outputs ({kb} KB)");
        }

        var deltaPct = TokensBefore > 0
            ? 100 - (TokensAfter * 100 / TokensBefore)
            : 0;
        writeInfo(
            $"✓ Compacted in {Duration.TotalMilliseconds:F0}ms — " +
            $"{MessagesAfter} messages, ~{TokensAfter} tokens (-{deltaPct}%)");
        writeInfo(sep);
    }
}
