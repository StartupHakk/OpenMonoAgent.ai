using OpenMono.Utils;

namespace OpenMono.Commands;

public sealed class ThinkCommand : ICommand
{
    public string Name => "think";
    public string Description => "Toggle / set thinking mode. No arg cycles levels (or toggles on binary models); /think [level] sets a level.";
    public CommandType Type => CommandType.Local;

    public Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var profile = ModelReasoningProfile.Resolve(context.Config.Llm.Model);

        if (profile.Kind == ReasoningKind.EffortLevels)
            CycleLevels(context, profile, args);
        else
            ToggleBinary(context);

        return Task.CompletedTask;
    }

    private static void CycleLevels(CommandContext context, ModelReasoningProfile profile, string[] args)
    {
        var levels = profile.Levels;
        var current = context.Session.Meta.ThinkingLevel ?? "off";
        var idx = Array.IndexOf(levels, current);
        if (idx < 0) idx = 0;

        var nextIdx = args.Length > 0
            ? ThinkingLevels.Resolve(args[0], levels)
            : (idx + 1) % levels.Length;

        if (nextIdx < 0)
        {
            context.Renderer.WriteWarning(
                $"Unknown level '{args[0]}'. Use: {string.Join(", ", levels)}");
            return;
        }

        var level = levels[nextIdx];
        context.Session.Meta.ThinkingLevel = level;
        context.Session.Meta.ThinkingEnabled = level != "off";

        context.Renderer.WriteInfo($"Thinking: {ThinkingLevels.Describe(level)}");
        if (level != "off")
            context.Renderer.WriteInfo("Note: thinking tokens use extra context. Use /think [level] to change.");
        if (level == "xhigh")
            context.Renderer.WriteInfo("Tip: Use /think medium for better speed on routine coding tasks.");
    }

    private static void ToggleBinary(CommandContext context)
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
    }
}
