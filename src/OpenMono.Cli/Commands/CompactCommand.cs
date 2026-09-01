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

        // Route through the loop so manual compaction emits the exact same signals as auto
        // (compacting indicator, checkpoint reset, token-gauge update, and — when a sink is
        // attached — the ACP compaction events). Hand-rolling Messages.Clear() here left the
        // checkpoint state stale and bypassed the sink entirely.
        var focus = args.Length > 0 ? string.Join(" ", args).Trim() : null;
        await _loop.RunManualCompactionAsync(focus, ct);
    }
}
