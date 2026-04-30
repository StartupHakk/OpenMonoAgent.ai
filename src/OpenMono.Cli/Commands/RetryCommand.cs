using OpenMono.Session;

namespace OpenMono.Commands;

public sealed class RetryCommand : ICommand
{
    private readonly ConversationLoop _loop;

    public RetryCommand(ConversationLoop loop) => _loop = loop;

    public string Name => "retry";
    public string Description => "Resend the last message and get a new response";
    public CommandType Type => CommandType.Local;

    public async Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var session = context.Session;

        var lastUserIdx = -1;
        for (var i = session.Messages.Count - 1; i >= 0; i--)
        {
            if (session.Messages[i].Role == MessageRole.User)
            {
                lastUserIdx = i;
                break;
            }
        }

        if (lastUserIdx < 0)
        {
            context.Renderer.WriteWarning("Nothing to retry — no previous message found.");
            return;
        }

        var lastUserText = session.Messages[lastUserIdx].Content;
        if (string.IsNullOrWhiteSpace(lastUserText))
        {
            context.Renderer.WriteWarning("Last message has no text content to retry.");
            return;
        }

        while (session.Messages.Count > lastUserIdx)
            session.Messages.RemoveAt(session.Messages.Count - 1);

        var preview = lastUserText.Length > 60
            ? lastUserText[..60].Replace('\n', ' ') + "…"
            : lastUserText.Replace('\n', ' ');
        context.Renderer.WriteInfo($"Retrying: {preview}");

        await _loop.RunTurnAsync(lastUserText, ct);
    }
}
