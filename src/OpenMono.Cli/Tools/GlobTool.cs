using System.Text.Json;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using OpenMono.Permissions;

namespace OpenMono.Tools;

public sealed class GlobTool : ToolBase
{
    public override string Name => "Glob";
    public override string Description => "Find files matching a glob pattern. Returns paths sorted by modification time.";
    public override bool IsConcurrencySafe => true;
    public override bool IsReadOnly => true;
    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("pattern", "Glob pattern (e.g. **/*.cs, src/**/*.json)")
        .AddString("path", "Directory to search in (default: working directory)")
        .Require("pattern");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input)
    {
        var searchPath = input.TryGetProperty("path", out var p) ? p.GetString() : ".";
        if (string.IsNullOrEmpty(searchPath))
            searchPath = ".";
        return [new FileReadCap(searchPath)];
    }

    protected override Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var pattern = input.GetProperty("pattern").GetString()!;
        var searchPath = input.TryGetProperty("path", out var p)
            ? Path.GetFullPath(p.GetString()!, context.WorkingDirectory)
            : context.WorkingDirectory;

        if (PathGuard.ValidateDirectory(searchPath, context.WorkingDirectory) is { } guardError)
            return Task.FromResult(ToolResult.Error(guardError));

        if (!Directory.Exists(searchPath))
            return Task.FromResult(ToolResult.Error($"Directory not found: {searchPath}"));

        try
        {
            var matcher = new Matcher();
            matcher.AddInclude(pattern);

            var directoryInfo = new DirectoryInfoWrapper(new DirectoryInfo(searchPath));
            var result = matcher.Execute(directoryInfo);

            var files = result.Files
                .Select(f => Path.Combine(searchPath, f.Path))
                .Where(File.Exists)
                .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                .Take(250)
                .ToList();

            if (files.Count == 0)
                return Task.FromResult(ToolResult.Success($"No files matching '{pattern}' in {searchPath}"));

            var output = string.Join('\n', files);
            return Task.FromResult(ToolResult.Success($"Found {files.Count} file(s):\n{output}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Error($"Glob error: {ex.Message}"));
        }
    }
}
