using OpenMono.Session;
using OpenMono.Utils;

namespace OpenMono.Commands;

public sealed class ModeCommand : ICommand
{
    public string Name => "mode";
    public string Description => "Toggle between Plan mode (read-only) and Build mode (execute actions)";
    public CommandType Type => CommandType.Local;

    public async Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var session = context.Session;
        await (session.Settings ?? Attach(context)).SetPlanModeAsync(!session.Meta.PlanMode, ct);
        var modeStr = session.Meta.PlanMode ? "PLAN" : "BUILD";
        Log.Info($"<---SWITCHED-TO-{modeStr}-MODE--->");

        // Notice in the conversation so the agent registers the switch on its next turn.
        session.AddMessage(new Message
        {
            Role = MessageRole.User,
            Content = session.Meta.PlanMode ? ModeInstructions.SwitchedToPlan : ModeInstructions.SwitchedToBuild,
        });

        if (session.Meta.PlanMode)
            context.Renderer.WriteInfo("✓ Switched to Plan mode — only read-only tools are available");
        else
            context.Renderer.WriteInfo("✓ Switched to Build mode — all tools are available (including FileWrite, Edit, etc.)");

        return;
    }

    private static OpenMono.Session.SessionSettings Attach(CommandContext context)
        => context.Session.Settings = OpenMono.Session.SessionSettings.Attach(context.Session, "tui");
}
