using OpenMono.Tui.Export;

namespace OpenMono.Commands;

public sealed class ExportCommand : ICommand
{
    public string Name => "export";
    public string Description => "Export conversation to file (markdown, json, html)";
    public CommandType Type => CommandType.Local;

    public Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var format = "markdown";
        string? outputPath = null;

        if (args.Length > 0)
            format = args[0].ToLowerInvariant();
        if (args.Length > 1)
            outputPath = args[1];

        var extension = format switch
        {
            "json" => "json",
            "html" => "html",
            "md" or "markdown" => "md",
            _ => null
        };

        if (extension is null)
        {
            context.Renderer.WriteWarning($"Unknown format: {format}. Use: markdown, json, or html");
            return Task.CompletedTask;
        }

        if (format == "md") format = "markdown";

        var session = context.Session;

        if (session.Messages.Count == 0)
        {
            context.Renderer.WriteWarning("Nothing to export — no messages in session.");
            return Task.CompletedTask;
        }

        if (outputPath is null)
        {
            var exportDir = Path.Combine(context.Config.DataDirectory, "exports");
            Directory.CreateDirectory(exportDir);
            outputPath = Path.Combine(exportDir, $"session-{session.Id}.{extension}");
        }
        else
        {

            if (outputPath.StartsWith('~'))
                outputPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    outputPath[1..].TrimStart('/'));

            var dir = Path.GetDirectoryName(outputPath);
            if (dir is not null)
                Directory.CreateDirectory(dir);
        }

        var content = format switch
        {
            "json" => JsonExporter.Export(session),
            "html" => HtmlExporter.Export(session),
            _ => MarkdownExporter.Export(session)
        };

        File.WriteAllText(outputPath, content);

        var fileInfo = new FileInfo(outputPath);
        var size = fileInfo.Length < 1024
            ? $"{fileInfo.Length} B"
            : $"{fileInfo.Length / 1024.0:F0} KB";

        context.Renderer.WriteInfo($"Exported to: {outputPath} ({size})");

        return Task.CompletedTask;
    }
}
