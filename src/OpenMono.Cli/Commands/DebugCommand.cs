namespace OpenMono.Commands;

public sealed class DebugCommand : ICommand
{
    public string Name => "debug";
    public string Description => "Toggle verbose debug output (LLM requests, SSE events, token counts)";
    public CommandType Type => CommandType.Local;

    public Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        context.Config.Verbose = !context.Config.Verbose;
        context.Renderer.Verbose = context.Config.Verbose;

        var state = context.Config.Verbose ? "ON" : "OFF";
        context.Renderer.WriteInfo($"Debug mode: {state}");
        return Task.CompletedTask;
    }
}
