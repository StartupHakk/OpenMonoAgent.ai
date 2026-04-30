using System.Text;
using OpenMono.Session;

namespace OpenMono.Tui.Export;

public static class MarkdownExporter
{
    public static string Export(SessionState session)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# OpenMono Session {session.Id}");
        sb.AppendLine();
        sb.AppendLine($"- **Started:** {session.StartedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"- **Turns:** {session.TurnCount}");
        sb.AppendLine($"- **Tokens:** ~{session.TotalTokensUsed:N0}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var msg in session.Messages)
        {
            if (msg.Role == MessageRole.System)
                continue;

            var header = msg.Role switch
            {
                MessageRole.User => "## User",
                MessageRole.Assistant => "## Assistant",
                MessageRole.Tool => $"### Tool: {msg.ToolName ?? "unknown"}",
                _ => $"## {msg.Role}"
            };

            sb.AppendLine(header);
            sb.AppendLine();

            if (!string.IsNullOrEmpty(msg.Content))
            {
                sb.AppendLine(msg.Content);
                sb.AppendLine();
            }

            if (msg.ToolCalls is { Count: > 0 })
            {
                foreach (var call in msg.ToolCalls)
                {
                    sb.AppendLine($"**Tool call:** `{call.Name}`");
                    sb.AppendLine();
                    sb.AppendLine("```json");
                    sb.AppendLine(call.Arguments);
                    sb.AppendLine("```");
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString();
    }
}
