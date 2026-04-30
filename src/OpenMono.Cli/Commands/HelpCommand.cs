using System.Text;

namespace OpenMono.Commands;

public sealed class HelpCommand : ICommand
{
    public string Name => "help";
    public string Description => "Show available commands";
    public CommandType Type => CommandType.Local;

    public Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Commands");
        sb.AppendLine();

        foreach (var cmd in context.CommandRegistry.All.OrderBy(c => c.Name))
        {
            var name = cmd.Name.TrimStart('/');
            sb.AppendLine($"  `/{name,-12}` — {cmd.Description}");
        }

        sb.AppendLine($"  `/{"quit",-12}` — Exit OpenMono");
        sb.AppendLine();
        sb.AppendLine("## Keyboard Shortcuts");
        sb.AppendLine();
        sb.AppendLine("  `Ctrl+C`        Clear input / clear context (if empty) / cancel turn");
        sb.AppendLine("  `Ctrl+C` twice  Exit");
        sb.AppendLine("  `Ctrl+U`        Kill current input line");
        sb.AppendLine("  `Ctrl+W`        Delete last word");
        sb.AppendLine("  `↑ / ↓`         Navigate input history");
        sb.AppendLine("  `Tab`           Autocomplete slash command");
        sb.AppendLine("  `Ctrl+P`        Open command picker");
        sb.AppendLine("  `PgUp / PgDn`   Scroll conversation");
        sb.AppendLine("  `Esc`           Cancel active LLM request");
        sb.AppendLine();
        sb.AppendLine($"Tools available: **{context.ToolRegistry.All.Count}** — " +
                      string.Join(", ", context.ToolRegistry.All.Select(t => t.Name)));
        sb.AppendLine();
        sb.Append("Tip: type `/` to browse commands interactively.");

        context.Renderer.WriteMarkdown(sb.ToString());
        return Task.CompletedTask;
    }
}
