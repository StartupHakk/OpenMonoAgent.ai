namespace OpenMono.Commands;

public sealed class ModelCommand : ICommand
{
    public string Name => "model";
    public string Description => "Switch model mid-session: /model <name>";
    public CommandType Type => CommandType.Local;

    public Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
        {
            context.Renderer.WriteInfo($"Current model: {context.Config.Llm.Model}");
            context.Renderer.WriteInfo("Usage: /model <model-name>");
            return Task.CompletedTask;
        }

        var newModel = args[0].Trim();
        var oldModel = context.Config.Llm.Model;
        context.Config.Llm.Model = newModel;

        context.Renderer.WriteInfo($"Model switched: {oldModel} → {newModel}");
        return Task.CompletedTask;
    }
}
