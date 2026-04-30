using OpenMono.Session;

namespace OpenMono.Commands;

public sealed class ClearCommand : ICommand
{
    public string Name => "clear";
    public string Description => "Clear conversation context and start fresh";
    public CommandType Type => CommandType.Local;

    public Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var session = context.Session;

        Message? systemPrompt = null;
        if (session.Messages.Count > 0 && session.Messages[0].Role == MessageRole.System)
            systemPrompt = session.Messages[0];

        session.Messages.Clear();

        if (systemPrompt is { } prompt)
            session.AddMessage(prompt);

        session.TotalTokensUsed = 0;
        session.TurnCount = 0;
        session.Todos.Clear();

        if (session.Meta.TokenTracker is not null)
        {
            session.Meta.TokenTracker = new Session.TokenTracker();
        }

        context.Renderer.ClearConversation();
        context.Renderer.WriteInfo("Context cleared. System prompt preserved.");

        return Task.CompletedTask;
    }
}
