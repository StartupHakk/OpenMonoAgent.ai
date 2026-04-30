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
                var preview = s.FirstMessage.Length > 60
                    ? s.FirstMessage[..60] + "..."
                    : s.FirstMessage;
                context.Renderer.WriteInfo(
                    $"  [{i + 1}] {s.StartedAt:yyyy-MM-dd HH:mm} UTC  " +
                    $"turns={s.TurnCount}  tokens={s.TotalTokens:N0}  id={s.Id}");
                if (!string.IsNullOrWhiteSpace(preview))
                    context.Renderer.WriteInfo($"      \"{preview}\"");
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

        var currentSystemMsg = context.Session.Messages
            .FirstOrDefault(m => m.Role == MessageRole.System);

        context.Session.Messages.Clear();

        if (currentSystemMsg is not null)
            context.Session.Messages.Add(currentSystemMsg);

        foreach (var msg in loaded.Messages.Where(m => m.Role != MessageRole.System))
            context.Session.AddMessage(msg);

        context.Session.TurnCount = loaded.TurnCount;
        context.Session.TotalTokensUsed = loaded.TotalTokensUsed;

        context.Session.Checkpoints.Clear();
        foreach (var cp in loaded.Checkpoints)
            context.Session.Checkpoints.Add(cp);
        context.Session.CheckpointCutoffIndex = loaded.CheckpointCutoffIndex;

        var cpInfo = loaded.Checkpoints.Count > 0
            ? $", {loaded.Checkpoints.Count} checkpoint(s) restored (cutoff=msg {loaded.CheckpointCutoffIndex})"
            : "";

        context.Renderer.WriteInfo(
            $"Resumed session {sessionId} — {loaded.TurnCount} turns, " +
            $"{context.Session.Messages.Count} messages loaded{cpInfo}.");
    }
}
