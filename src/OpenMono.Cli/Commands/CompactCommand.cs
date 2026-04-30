using OpenMono.Session;

namespace OpenMono.Commands;

public sealed class CompactCommand : ICommand
{
    private readonly Compactor _compactor;

    public CompactCommand(Compactor compactor) => _compactor = compactor;

    public string Name => "compact";
    public string Description => "Summarize conversation history to free context space";
    public CommandType Type => CommandType.Local;

    public async Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var session = context.Session;

        if (session.Messages.Count < 6)
        {
            context.Renderer.WriteWarning("Conversation is too short to compact (need at least 6 messages).");
            return;
        }

        var before = session.Messages.Count;
        context.Renderer.WriteInfo("Compacting conversation history...");

        var compacted = await _compactor.CompactAsync(session, ct);

        session.Messages.Clear();
        foreach (var msg in compacted.Messages)
            session.AddMessage(msg);

        var freed = before - session.Messages.Count;
        context.Renderer.WriteInfo($"Compacted: {before} → {session.Messages.Count} messages ({freed} removed). Context freed.");
    }
}
