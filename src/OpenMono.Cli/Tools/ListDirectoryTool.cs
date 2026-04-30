using System.Text.Json;
using OpenMono.Permissions;

namespace OpenMono.Tools;

public sealed class ListDirectoryTool : ToolBase
{
    public override string Name => "ListDirectory";
    public override string Description => "List files and directories at a given path. Shows file sizes and modification times.";
    public override bool IsConcurrencySafe => true;
    public override bool IsReadOnly => true;
    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("path", "Directory path to list (default: working directory)")
        .AddBoolean("recursive", "List recursively (default: false)")
        .AddInteger("max_entries", "Maximum entries to return (default: 200)");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input)
    {
        var dirPath = input.TryGetProperty("path", out var p) ? p.GetString() : ".";
        if (string.IsNullOrEmpty(dirPath))
            dirPath = ".";
        return [new FileReadCap(dirPath)];
    }

    protected override Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var dirPath = input.TryGetProperty("path", out var p)
            ? Path.GetFullPath(p.GetString()!, context.WorkingDirectory)
            : context.WorkingDirectory;
        var recursive = input.TryGetProperty("recursive", out var r) && r.GetBoolean();
        var maxEntries = input.TryGetProperty("max_entries", out var m) ? m.GetInt32() : 200;

        if (PathGuard.ValidateDirectory(dirPath, context.WorkingDirectory) is { } guardError)
            return Task.FromResult(ToolResult.Error(guardError));

        if (!Directory.Exists(dirPath))
            return Task.FromResult(ToolResult.Error($"Directory not found: {dirPath}"));

        try
        {
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var entries = new List<string>();

            foreach (var dir in Directory.EnumerateDirectories(dirPath, "*", searchOption))
            {
                if (entries.Count >= maxEntries) break;
                var rel = Path.GetRelativePath(dirPath, dir);
                entries.Add($"  {rel}/");
            }

            foreach (var file in Directory.EnumerateFiles(dirPath, "*", searchOption))
            {
                if (entries.Count >= maxEntries) break;
                var rel = Path.GetRelativePath(dirPath, file);
                var info = new FileInfo(file);
                var size = FormatSize(info.Length);
                entries.Add($"  {rel}  ({size})");
            }

            if (entries.Count == 0)
                return Task.FromResult(ToolResult.Success($"{dirPath}/ (empty)"));

            var truncated = entries.Count >= maxEntries ? $"\n... (truncated at {maxEntries} entries)" : "";
            var header = $"{dirPath}/ ({entries.Count} entries)";
            return Task.FromResult(ToolResult.Success($"{header}\n{string.Join('\n', entries)}{truncated}"));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(ToolResult.Error($"Permission denied: {dirPath}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error($"Error listing directory: {ex.Message}"));
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes}B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1}KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1}MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1}GB",
    };
}
