using OpenMono.Session;

namespace OpenMono.Commands;

public sealed class CompactCommand : ICommand
{
    private readonly ConversationLoop _loop;

    public CompactCommand(ConversationLoop loop) => _loop = loop;

    public string Name => "compact";
    public string Description => "Summarize conversation history to free context space. Optional focus: /compact focus on auth code";
    public CommandType Type => CommandType.Local;

    public async Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var session = context.Session;

        if (session.Messages.Count < 6)
        {
            context.Renderer.WriteWarning("Conversation is too short to compact (need at least 6 messages).");
            return;
        }

        var focus = args.Length > 0 ? string.Join(" ", args).Trim() : null;
        await _loop.RunManualCompactionAsync(focus, ct);
    }
}
