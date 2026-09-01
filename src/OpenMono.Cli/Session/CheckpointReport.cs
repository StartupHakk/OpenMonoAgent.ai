namespace OpenMono.Session;

// The soft, non-destructive context reduction that fires before compaction (at ~65% of the
// window). Unlike CompactionReport, a checkpoint keeps the recent turns verbatim and only folds
// the older history into a summary, so the block surfaces what was compressed and what was kept
// rather than before/after token counts. Rendered as a bordered block so it reads as a distinct,
// strong event in the TUI — mirroring CompactionReport.RenderTo's style.
public sealed record CheckpointReport
{
    public required int CheckpointIndex { get; init; }
    public required int MessagesCompressed { get; init; }
    public required int MessagesKept { get; init; }
    public required TimeSpan Duration { get; init; }
    // What triggered it: "pre-turn" (before the outgoing request) or "mid-turn" (between tool
    // steps). Lets the user tell the two trigger sites apart.
    public required string Trigger { get; init; }
    // The generated summary of the compressed older history (what the model continues from).
    public string? SummaryText { get; init; }

    public void RenderTo(Action<string> writeInfo)
    {
        const string sep = "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━";
        writeInfo(sep);
        writeInfo($"🗜  Checkpoint #{CheckpointIndex} — context window filling up ({Trigger})");
        writeInfo(sep);

        if (MessagesCompressed == 0)
        {
            writeInfo("Nothing to checkpoint — conversation too short or already checkpointed.");
            writeInfo(sep);
            return;
        }

        writeInfo($"Compressed {MessagesCompressed} older messages → summary");
        writeInfo($"   • Kept {MessagesKept} most recent messages verbatim");
        writeInfo($"   • Done in {Duration.TotalMilliseconds:F0}ms");
        writeInfo(sep);

        if (!string.IsNullOrWhiteSpace(SummaryText))
        {
            writeInfo("📝 Summary of compressed history:");
            foreach (var line in SummaryText!.Split('\n'))
                writeInfo(line);
            writeInfo(sep);
        }
    }
}
