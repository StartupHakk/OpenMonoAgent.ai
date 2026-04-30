using System.Text.Json;
using OpenMono.Permissions;
using OpenMono.Utils;

namespace OpenMono.Tools;

public sealed class FileWriteTool : ToolBase
{
    public override string Name => "FileWrite";
    public override string Description => "Create a new file or overwrite an existing file with the provided content.";

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("file_path", "Absolute path to the file to write")
        .AddString("content", "The content to write to the file")
        .Require("file_path", "content");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input)
    {
        var filePath = input.TryGetProperty("file_path", out var fp) ? fp.GetString() : null;
        if (string.IsNullOrEmpty(filePath))
            return [];

        return [new FileWriteCap(filePath, "modify")];
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var filePath = input.GetProperty("file_path").GetString()!;
        var content = input.GetProperty("content").GetString()!;
        var resolvedPath = Path.GetFullPath(filePath, context.WorkingDirectory);

        if (PathGuard.Validate(resolvedPath, context.WorkingDirectory) is { } guardError)
            return ToolResult.Error(guardError);

        try
        {
            var dir = Path.GetDirectoryName(resolvedPath);
            if (dir is not null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var existed = File.Exists(resolvedPath);
            var oldContent = existed ? await File.ReadAllTextAsync(resolvedPath, ct) : null;

            var secrets = SecretScanner.Scan(content);
            var secretWarning = secrets.Count > 0
                ? $"\n⚠ Potential secret(s) detected: {string.Join(", ", secrets.Select(SecretScanner.RuleIdToLabel))}. " +
                  "Verify this file should contain credentials before committing."
                : string.Empty;

            context.FileHistory?.RecordBefore(resolvedPath, Name, context.Session.Messages.Count);

            await File.WriteAllTextAsync(resolvedPath, content, ct);

            context.FileHistory?.RecordAfter(resolvedPath);

            var lineCount = content.Split('\n').Length;
            var verb = existed ? "Overwrote" : "Created";
            var diff = oldContent is null
                ? InlineDiff.FromNewFile(content, resolvedPath)
                : InlineDiff.FromOverwrite(oldContent, content, resolvedPath);
            return ToolResult.Success($"{verb} {resolvedPath} ({lineCount} lines){secretWarning}")
                .WithDiff(diff);
        }
        catch (UnauthorizedAccessException)
        {
            return ToolResult.Error(DiagnoseWriteFailure(resolvedPath));
        }
        catch (IOException ex) when (ex.HResult == unchecked((int)0x80070020)||
                                     ex.Message.Contains("being used by another process"))
        {
            return ToolResult.Error($"Cannot write to '{resolvedPath}': file is locked by another process.");
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Error writing file: {ex.Message}");
        }
    }

    private static string DiagnoseWriteFailure(string path)
    {
        try
        {
            if (File.Exists(path) && new FileInfo(path).IsReadOnly)
            {
                return OperatingSystem.IsWindows()
                    ? $"Cannot write to '{path}': file is read-only. Run in your terminal: attrib -r \"{path}\""
                    : $"Cannot write to '{path}': file has no write permission. Run in your terminal: chmod u+w {path}";
            }
        }
        catch { }

        return $"Cannot write to '{path}': access denied. Check ownership with: ls -la {path}";
    }
}
