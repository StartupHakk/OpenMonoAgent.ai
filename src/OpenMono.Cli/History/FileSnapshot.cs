namespace OpenMono.History;

public sealed record FileSnapshot
{
    public required string FilePath { get; init; }
    public required string? ContentBefore { get; init; }
    public required string ContentAfter { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string ToolName { get; init; }
    public required int MessageIndex { get; init; }
    public bool IsCreation => ContentBefore is null;
}
