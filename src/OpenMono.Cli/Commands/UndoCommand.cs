using OpenMono.History;

namespace OpenMono.Commands;

public sealed class UndoCommand : ICommand
{
    public string Name => "undo";
    public string Description => "Revert the last file modification(s). Usage: /undo [count]";
    public CommandType Type => CommandType.Local;

    public async Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var history = context.Session.Meta.FileHistory;
        if (history is null)
        {
            context.Renderer.WriteInfo("No file history available.");
            return;
        }

        if (history.Snapshots.Count == 0)
        {
            context.Renderer.WriteInfo("No file modifications to undo.");
            return;
        }

        var count = 1;
        if (args.Length > 0 && int.TryParse(args[0], out var n))
            count = Math.Max(1, Math.Min(n, history.Snapshots.Count));

        var recent = history.GetRecentChanges(count);
        context.Renderer.WriteInfo($"Will revert {count} change(s):");
        foreach (var change in recent.Take(count))
            context.Renderer.WriteInfo(change);

        var answer = await context.Renderer.AskUserAsync("Proceed? [y/N]", ct);
        if (!answer.Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            context.Renderer.WriteInfo("Cancelled.");
            return;
        }

        var reverted = await history.RevertAsync(count, ct);
        foreach (var msg in reverted)
            context.Renderer.WriteInfo(msg);

        context.Renderer.WriteInfo($"Done. {reverted.Count} file(s) reverted.");
    }
}
