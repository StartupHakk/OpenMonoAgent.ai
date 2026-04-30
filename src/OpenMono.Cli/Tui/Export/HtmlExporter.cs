using System.Text;
using System.Web;
using OpenMono.Session;

namespace OpenMono.Tui.Export;

public static class HtmlExporter
{
    public static string Export(SessionState session)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>OpenMono Session {Esc(session.Id)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(Css);
        sb.AppendLine("</style></head><body>");

        sb.AppendLine($"<h1>OpenMono Session {Esc(session.Id)}</h1>");
        sb.AppendLine("<div class=\"meta\">");
        sb.AppendLine($"<p>Started: {session.StartedAt:yyyy-MM-dd HH:mm:ss} UTC &middot; ");
        sb.AppendLine($"Turns: {session.TurnCount} &middot; ");
        sb.AppendLine($"Tokens: ~{session.TotalTokensUsed:N0}</p>");
        sb.AppendLine("</div><hr>");

        foreach (var msg in session.Messages)
        {
            if (msg.Role == MessageRole.System) continue;

            var roleClass = msg.Role.ToString().ToLowerInvariant();
            var label = msg.Role switch
            {
                MessageRole.Tool => $"Tool: {Esc(msg.ToolName ?? "unknown")}",
                _ => msg.Role.ToString()
            };

            sb.AppendLine($"<div class=\"message {roleClass}\">");
            sb.AppendLine($"<div class=\"role\">{label}</div>");

            if (!string.IsNullOrEmpty(msg.Content))
            {
                sb.AppendLine("<div class=\"content\">");
                sb.AppendLine(FormatContent(msg.Content));
                sb.AppendLine("</div>");
            }

            if (msg.ToolCalls is { Count: > 0 })
            {
                foreach (var call in msg.ToolCalls)
                {
                    sb.AppendLine($"<div class=\"tool-call\"><strong>{Esc(call.Name)}</strong>");
                    sb.AppendLine($"<pre><code>{Esc(call.Arguments)}</code></pre></div>");
                }
            }

            sb.AppendLine("</div>");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string FormatContent(string content)
    {
        var sb = new StringBuilder();
        var inCodeBlock = false;
        string? codeLang = null;

        foreach (var rawLine in content.Split('\n'))
        {
            if (rawLine.StartsWith("```"))
            {
                if (!inCodeBlock)
                {
                    codeLang = rawLine.Length > 3 ? Esc(rawLine[3..].Trim()) : null;
                    var langAttr = codeLang is not null ? $" class=\"lang-{codeLang}\"" : "";
                    sb.AppendLine($"<pre><code{langAttr}>");
                    inCodeBlock = true;
                }
                else
                {
                    sb.AppendLine("</code></pre>");
                    inCodeBlock = false;
                    codeLang = null;
                }
                continue;
            }

            if (inCodeBlock)
            {
                sb.AppendLine(Esc(rawLine));
            }
            else
            {
                sb.Append("<p>").Append(Esc(rawLine)).AppendLine("</p>");
            }
        }

        if (inCodeBlock)
            sb.AppendLine("</code></pre>");

        return sb.ToString();
    }

    private static string Esc(string s) => HttpUtility.HtmlEncode(s);

    private const string Css = """
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
               max-width: 900px; margin: 2rem auto; padding: 0 1rem;
               background: #1e1e1e; color: #d4d4d4; }
        h1 { color: #569cd6; border-bottom: 1px solid #333; padding-bottom: 0.5rem; }
        .meta { color: #808080; font-size: 0.9rem; }
        hr { border: none; border-top: 1px solid #333; margin: 1.5rem 0; }
        .message { margin: 1rem 0; padding: 1rem; border-radius: 8px; border: 1px solid #333; }
        .message.user { border-left: 3px solid #6aaf6a; }
        .message.assistant { border-left: 3px solid #5c9fd8; }
        .message.tool { border-left: 3px solid #808080; margin-left: 1rem; font-size: 0.9rem; }
        .role { font-weight: bold; margin-bottom: 0.5rem; color: #808080; text-transform: uppercase;
                font-size: 0.8rem; letter-spacing: 0.05em; }
        .content p { margin: 0.3rem 0; line-height: 1.6; }
        pre { background: #282c34; padding: 1rem; border-radius: 4px; overflow-x: auto; }
        code { font-family: 'SF Mono', 'Fira Code', 'Cascadia Code', monospace; font-size: 0.9rem; }
        .tool-call { background: #252525; padding: 0.5rem; border-radius: 4px; margin: 0.5rem 0; }
        .tool-call strong { color: #d19a66; }
        """;
}
