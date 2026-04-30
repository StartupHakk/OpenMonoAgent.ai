namespace OpenMono.Commands;

public sealed class StatusCommand : ICommand
{
    public string Name => "status";
    public string Description => "Show current session status";
    public CommandType Type => CommandType.Local;

    public Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var session = context.Session;
        context.Renderer.WriteInfo($"Session: {session.Id}");
        context.Renderer.WriteInfo($"Started: {session.StartedAt:yyyy-MM-dd HH:mm:ss} UTC");
        context.Renderer.WriteInfo($"Turns: {session.TurnCount}");
        context.Renderer.WriteInfo($"Messages: {session.Messages.Count}");
        context.Renderer.WriteInfo($"Tokens used: ~{session.TotalTokensUsed:N0}");
        context.Renderer.WriteInfo($"Model: {context.Config.Llm.Model}");
        context.Renderer.WriteInfo($"Endpoint: {context.Config.Llm.Endpoint}");
        context.Renderer.WriteInfo($"Working dir: {context.Config.WorkingDirectory}");

        if (session.Todos.Count > 0)
        {
            context.Renderer.WriteInfo("");
            context.Renderer.WriteTodos(session.Todos);
        }

        return Task.CompletedTask;
    }
}
