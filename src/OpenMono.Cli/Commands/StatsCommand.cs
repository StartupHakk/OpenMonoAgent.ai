namespace OpenMono.Commands;

public sealed class StatsCommand : ICommand
{
    public string Name => "stats";
    public string Description => "Show token usage and tool statistics for the current session";
    public CommandType Type => CommandType.Local;

    public Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var tracker = context.Session.Meta.TokenTracker;

        if (tracker is null || tracker.ApiCalls == 0)
        {
            context.Renderer.WriteInfo("No usage data yet. Start a conversation first.");
            return Task.CompletedTask;
        }

        var summary = tracker.GetSummary(context.Session.StartedAt);
        context.Renderer.WriteMarkdown($"```\n{summary}\n```");
        return Task.CompletedTask;
    }
}
