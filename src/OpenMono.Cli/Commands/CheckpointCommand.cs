using OpenMono.Session;

namespace OpenMono.Commands;

public sealed class CheckpointCommand : ICommand
{
    private readonly Checkpointer _checkpointer;

    public CheckpointCommand(Checkpointer checkpointer) => _checkpointer = checkpointer;

    public string Name => "checkpoint";
    public string Description => "Summarise conversation history into a checkpoint to free up context window";
    public CommandType Type => CommandType.Local;

    public async Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var session = context.Session;
        var nonSystemCount = session.Messages.Count(m => m.Role != MessageRole.System);

        if (nonSystemCount < 2 || !_checkpointer.HasCompressibleContent(session))
        {
            context.Renderer.WriteWarning(
                "Nothing to checkpoint — all messages are within the recent-turns window.");
            return;
        }

        context.Renderer.WriteInfo("Creating checkpoint...");

        try
        {
            var entry = await _checkpointer.CreateCheckpointAsync(session, ct);
            context.Renderer.WriteInfo(
                $"Checkpoint #{session.Checkpoints.Count} stored — " +
                $"{entry.MessagesCompressed} messages compressed (turn {entry.TurnIndex}, id={entry.Id}).");

            var sessionManager = new SessionManager(context.Config);
            await sessionManager.SaveAsync(session, ct);
        }
        catch (OperationCanceledException)
        {
            context.Renderer.WriteWarning("Checkpoint cancelled.");
        }
    }
}
