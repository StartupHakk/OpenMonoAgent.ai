using OpenMono.Session;

namespace OpenMono.Commands;

public sealed class ResumeCommand : ICommand
{
    public string Name => "resume";
    public string Description => "Resume a previous session (/resume [id])";
    public CommandType Type => CommandType.Local;

    public async Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var sessionManager = new SessionManager(context.Config);

        string? sessionId = args.Length > 0 ? args[0].Trim() : null;

        if (sessionId is null)
        {
            var sessions = await sessionManager.ListSessionsAsync(10, ct);

            if (sessions.Count == 0)
            {
                context.Renderer.WriteWarning("No saved sessions found.");
                return;
            }

            context.Renderer.WriteInfo("");
            context.Renderer.WriteInfo("Recent sessions:");
            context.Renderer.WriteInfo("");

            for (var i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                var title = !string.IsNullOrWhiteSpace(s.Title) ? s.Title : s.FirstMessage;
                if (title.Length > 60) title = title[..60] + "...";
                var model = string.IsNullOrWhiteSpace(s.Model) ? "" : $"  model={s.Model}";
                context.Renderer.WriteInfo(
                    $"  [{i + 1}] {s.LastActivityAt:yyyy-MM-dd HH:mm} UTC  " +
                    $"turns={s.TurnCount}  msgs={s.MessageCount}{model}  id={s.Id}");
                if (!string.IsNullOrWhiteSpace(title))
                    context.Renderer.WriteInfo($"      \"{title}\"");
            }

            context.Renderer.WriteInfo("");
            var answer = await context.Renderer.AskUserAsync(
                "Enter session number or ID (Enter to cancel):", ct);

            if (string.IsNullOrWhiteSpace(answer))
                return;

            var cleaned = answer.Trim();
            if (cleaned.StartsWith('/'))
                cleaned = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? cleaned;

            if (int.TryParse(cleaned, out var idx) && idx >= 1 && idx <= sessions.Count)
                sessionId = sessions[idx - 1].Id;
            else
                sessionId = cleaned;
        }

        var loaded = await sessionManager.LoadAsync(sessionId, ct);
        if (loaded is null)
        {
            context.Renderer.WriteWarning($"Session '{sessionId}' not found.");
            return;
        }

        SessionReattach.Apply(context.Session, loaded);

        var cpInfo = context.Session.Checkpoints.Count > 0
            ? $", {context.Session.Checkpoints.Count} checkpoint(s) restored " +
              $"(cutoff=msg {context.Session.CheckpointCutoffIndex})"
            : "";

        context.Renderer.WriteInfo(
            $"Resumed session {context.Session.Id} — {context.Session.TurnCount} turns, " +
            $"{context.Session.Messages.Count} messages loaded{cpInfo}.");
    }
}
