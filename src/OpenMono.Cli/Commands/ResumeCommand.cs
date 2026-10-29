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

        string sessionId;

        if (args.Length > 0)
        {
            sessionId = args[0].Trim();
        }
        else
        {
            var sessions = await sessionManager.ListSessionsAsync(10, ct);

            if (sessions.Count == 0)
            {
                context.Renderer.WriteWarning("No saved sessions found.");
                return;
            }

            var choices = sessions.Select(s =>
            {
                var title = !string.IsNullOrWhiteSpace(s.Title) ? s.Title : s.FirstMessage;
                if (!string.IsNullOrWhiteSpace(title) && title.Length > 40) title = title[..40] + "...";
                var model = string.IsNullOrWhiteSpace(s.Model) ? "" : $"  {s.Model}";
                var t = string.IsNullOrWhiteSpace(title) ? "" : $"  \"{title}\"";
                return $"{s.LastActivityAt:yyyy-MM-dd HH:mm}  {s.TurnCount}t/{s.MessageCount}m  {s.Id}{model}{t}";
            }).ToList();

            var picked = await context.Renderer.SelectOptionAsync("Resume session", choices, 0, ct);
            if (picked is null)
            {
                context.Renderer.WriteInfo("Session selection cancelled.");
                return;
            }

            sessionId = sessions[0].Id;
            for (var i = 0; i < sessions.Count; i++)
            {
                if (string.Equals(choices[i], picked, StringComparison.Ordinal))
                {
                    sessionId = sessions[i].Id;
                    break;
                }
            }
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
