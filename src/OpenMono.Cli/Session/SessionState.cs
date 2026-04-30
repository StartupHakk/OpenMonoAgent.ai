namespace OpenMono.Session;

public sealed class SessionState
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public List<Message> Messages { get; } = [];
    public SessionMetadata Meta { get; } = new();
    public List<TodoItem> Todos { get; } = [];
    public int TotalTokensUsed { get; set; }
    public int TurnCount { get; set; }

    public List<CheckpointEntry> Checkpoints { get; } = [];

    public int CheckpointCutoffIndex { get; set; }

    public void AddMessage(Message message) => Messages.Add(message);
}

public sealed record TodoItem
{
    public required string Content { get; init; }
    public required string Status { get; init; }
    public string? ActiveForm { get; init; }
}
