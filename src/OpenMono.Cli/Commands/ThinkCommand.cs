using OpenMono.Session;
using OpenMono.Utils;

namespace OpenMono.Commands;

public sealed class ThinkCommand : ICommand, ICommandArgumentSource
{
    public string Name => "think";
    public string Description => "Toggle / set thinking mode. No arg cycles levels (or toggles on binary models); /think [level] sets a level.";
    public CommandType Type => CommandType.Local;

    /// <summary>Suggests the current model's reasoning levels (effort levels or on/off).
    /// Model-backed via <see cref="ModelReasoningProfile"/>.</summary>
    public IEnumerable<(string Value, string Detail)> GetArgumentOptions(string query, Config.AppConfig config)
    {
        var profile = Profile(config, session: null);
        if (profile.Kind == ReasoningKind.EffortLevels)
        {
            foreach (var level in profile.Levels)
                yield return (level, ThinkingLevels.Describe(level));
        }
        else
        {
            yield return ("on", "Enable step-by-step reasoning");
            yield return ("off", "Respond directly");
        }
    }

    public async Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var settings = context.Session.Settings ?? Attach(context);

        ThinkingResult result;
        if (args.Length > 0 && args[0].Trim().Length > 0)
            result = await settings.SetThinkingAsync(args[0].Trim(), ct);
        else
            result = await settings.CycleThinkingAsync(ct: ct);

        if (!result.Applied && result.Error is not null)
        {
            context.Renderer.WriteWarning(result.Error);
            return;
        }

        var level = context.Session.Meta.ThinkingLevel;
        context.Renderer.WriteInfo($"Thinking: {ThinkingLevels.Describe(level ?? "off")}");
        if (level is not null && level != "off")
            context.Renderer.WriteInfo("Note: thinking tokens use extra context. Use /think [level] to change.");
        if (level == "xhigh")
            context.Renderer.WriteInfo("Tip: Use /think medium for better speed on routine coding tasks.");
    }

    private static SessionSettings Attach(CommandContext context)
        => context.Session.Settings = SessionSettings.Attach(
            context.Session, "tui", reasoningProfile: () => Profile(context.Config, context.Session));

    /// <summary>Server-advertised capabilities first, name heuristics as fallback.</summary>
    private static ModelReasoningProfile Profile(Config.AppConfig config, SessionState? session)
        => ModelReasoningProfile.Resolve(
            session?.Model ?? config.Llm.Model,
            config.Capabilities.Get(config.Llm.Endpoint, session?.Model) ?? config.Llm.ServerReasoning);
}
