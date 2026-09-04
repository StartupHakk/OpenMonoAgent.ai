namespace OpenMono.Session;

public sealed record CheckpointReport
{
    public required int CheckpointIndex { get; init; }
    public required int MessagesCompressed { get; init; }
    public required int MessagesKept { get; init; }
    public required TimeSpan Duration { get; init; }
    public required string Trigger { get; init; }
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
