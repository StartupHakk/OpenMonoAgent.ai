namespace OpenMono.Session;

public static class SessionReattach
{
    public static void Apply(SessionState live, SessionState loaded)
    {
        var liveSystems = live.Messages.Where(m => m.Role == MessageRole.System).ToList();
        var systemMessages = liveSystems.Count > 0
            ? liveSystems
            : loaded.Messages.Where(m => m.Role == MessageRole.System).ToList();

        live.Id = loaded.Id;
        live.StartedAt = loaded.StartedAt;
        live.Model = loaded.Model;

        live.Messages.Clear();
        foreach (var sys in systemMessages)
            live.Messages.Add(sys);
        foreach (var msg in loaded.Messages.Where(m => m.Role != MessageRole.System))
            live.Messages.Add(msg);

        live.TurnCount = loaded.TurnCount;
        live.TotalTokensUsed = loaded.TotalTokensUsed;

        live.Checkpoints.Clear();
        foreach (var cp in loaded.Checkpoints)
            live.Checkpoints.Add(cp);
        live.CheckpointCutoffIndex = loaded.CheckpointCutoffIndex;

        live.Todos.Clear();
        foreach (var todo in loaded.Todos)
            live.Todos.Add(todo);

        live.Meta.PlanMode = loaded.Meta.PlanMode;

        SessionConsistency.Repair(live);
    }
}
