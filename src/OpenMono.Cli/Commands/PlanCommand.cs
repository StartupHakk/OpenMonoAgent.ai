using OpenMono.Session;

namespace OpenMono.Commands;

public sealed class PlanCommand : ICommand
{
    private readonly ConversationLoop _loop;

    public PlanCommand(ConversationLoop loop) => _loop = loop;

    public string Name => "plan";
    public string Description => "Enter Plan mode (read-only); '/plan <task>' plans a task right away";
    public CommandType Type => CommandType.Local;

    public async Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var session = context.Session;
        var task = args.Length > 0 ? string.Join(' ', args).Trim() : "";

        var wasPlanMode = session.Meta.PlanMode;
        session.Meta.PlanMode = true;

        // Notice in the conversation so the agent registers the switch on its next turn.
        if (!wasPlanMode)
            session.AddMessage(new Message
            {
                Role = MessageRole.User,
                Content = ModeInstructions.SwitchedToPlan,
            });

        context.Renderer.WriteInfo(
            "✓ Plan mode — read-only. I'll investigate and present a plan for your approval before any changes.");

        if (task.Length == 0)
            return;

        // Drive a planning turn immediately: investigate, then present via CreatePlan (which
        // renders the plan to the user). The agent stays in Plan mode until the user approves.
        var instruction =
            $"{task}\n\n" +
            "[Plan this task: investigate as needed, then call CreatePlan to present a numbered " +
            "implementation plan for my approval. Do not implement anything yet.]";

        await _loop.RunTurnAsync(instruction, null, ct);
    }
}
