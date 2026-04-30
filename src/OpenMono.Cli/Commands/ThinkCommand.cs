namespace OpenMono.Commands;

public sealed class ThinkCommand : ICommand
{
    public string Name => "think";
    public string Description => "Toggle thinking mode (step-by-step reasoning). Default: off.";
    public CommandType Type => CommandType.Local;

    public Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        context.Session.Meta.ThinkingEnabled = !context.Session.Meta.ThinkingEnabled;

        if (context.Session.Meta.ThinkingEnabled)
        {
            context.Renderer.WriteInfo("Thinking mode ON — model will reason step-by-step before responding.");
            context.Renderer.WriteInfo("Note: thinking tokens use extra context. Use for complex tasks only.");
        }
        else
        {
            context.Renderer.WriteInfo("Thinking mode OFF — model responds directly (default).");
        }

        return Task.CompletedTask;
    }
}
