namespace OpenMono.Commands;

public sealed class DoomLoopCommand : ICommand
{
    public string Name => "doomloop";
    public string Description => "Toggle doom loop detection (/doomloop [on|off]). Default: on.";
    public CommandType Type => CommandType.Local;

    public Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var enabled = args.Length > 0
            ? args[0].ToLowerInvariant() switch
            {
                "on" or "an" or "true" or "1" => true,
                "off" or "aus" or "false" or "0" => false,
                _ => (bool?)null,
            }
            : !context.Session.Meta.DoomLoopDetection;

        if (enabled is null)
        {
            context.Renderer.WriteWarning($"Unknown argument '{args[0]}'. Usage: /doomloop [on|off]");
            return Task.CompletedTask;
        }

        context.Session.Meta.DoomLoopDetection = enabled.Value;

        if (enabled.Value)
        {
            context.Renderer.WriteInfo("Doom loop detection ON — aborts if the same tool calls repeat 3× (default).");
        }
        else
        {
            context.Renderer.WriteInfo("Doom loop detection OFF — repetitive tool calls will NOT be aborted.");
            context.Renderer.WriteInfo("Useful for repetitive work like translations. Re-enable with /doomloop on.");
        }

        return Task.CompletedTask;
    }
}
