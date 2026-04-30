namespace OpenMono.Session;

public sealed record CheckpointEntry
{
    public required string Id { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required int TurnIndex { get; init; }

    public required int CutoffMessageIndex { get; init; }

    public required string Summary { get; init; }

    public int MessagesCompressed { get; init; }
}
