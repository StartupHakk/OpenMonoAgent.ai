namespace OpenMono.Session;

public sealed class SessionState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public string? Model { get; set; }
    public List<Message> Messages { get; } = [];
    public SessionMetadata Meta { get; } = new();
    public List<TodoItem> Todos { get; } = [];
    public int TotalTokensUsed { get; set; }
    public int TurnCount { get; set; }

    /// <summary>
    /// Id of the tool call currently executing. Set by the tool dispatcher before a
    /// tool runs so a tool can stamp per-call state (e.g. diff staging). Not persisted.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CurrentToolCallId { get; set; }

    /// <summary>
    /// Lazy per-session diff stager. Created on first access from the session's
    /// <c>AppConfig.DataDirectory</c> and reused for the session's lifetime, so a
    /// write tool can persist a staged diff without every ToolContext wiring site
    /// being updated. Not persisted (reloaded from history.json on resume).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SessionDiffStager? DiffStager { get; set; }

    public SessionDiffStager GetDiffStager(string dataDir)
    {
        if (DiffStager is null)
            DiffStager = new SessionDiffStager(dataDir, Id);
        return DiffStager;
    }

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
