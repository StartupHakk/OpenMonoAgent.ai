using OpenMono.Session;

namespace OpenMono.Commands;

public sealed class CompactCommand : ICommand
{
    private readonly Compactor _compactor;

    public CompactCommand(Compactor compactor) => _compactor = compactor;

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
        var (compacted, report) = await _compactor.CompactAsync(session, focus, ct);

        session.Messages.Clear();
        foreach (var msg in compacted.Messages)
            session.AddMessage(msg);

        var lastPromptTokens = session.Meta.TokenTracker?.LastPromptTokens ?? 0;
        report.RenderTo(context.Renderer.WriteInfo, lastPromptTokens);
    }
}
