using System.Diagnostics;
using System.Text.Json;
using OpenMono.Permissions;

namespace OpenMono.Tools;

public sealed class GrepTool : ToolBase
{
    public override string Name => "Grep";
    public override string Description => "Search file contents using regex patterns. Uses ripgrep for fast, recursive search.";
    public override bool IsConcurrencySafe => true;
    public override bool IsReadOnly => true;
    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("pattern", "Regex pattern to search for")
        .AddString("path", "File or directory to search in")
        .AddString("glob", "Glob filter for files (e.g. *.cs)")
        .AddBoolean("case_insensitive", "Case insensitive search")
        .AddInteger("context_lines", "Lines of context around matches")
        .AddInteger("max_results", "Maximum number of results (default: 250)")
        .Require("pattern");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input)
    {
        var searchPath = input.TryGetProperty("path", out var p) ? p.GetString() : ".";
        if (string.IsNullOrEmpty(searchPath))
            searchPath = ".";
        return [new FileReadCap(searchPath)];
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var pattern = input.GetProperty("pattern").GetString()!;
        var searchPath = input.TryGetProperty("path", out var p)
            ? Path.GetFullPath(p.GetString()!, context.WorkingDirectory)
            : context.WorkingDirectory;

        if (PathGuard.ValidateDirectory(searchPath, context.WorkingDirectory) is { } guardError)
            return ToolResult.Error(guardError);
        var glob = input.TryGetProperty("glob", out var g) ? g.GetString() : null;
        var caseInsensitive = input.TryGetProperty("case_insensitive", out var ci) && ci.GetBoolean();
        var contextLines = input.TryGetProperty("context_lines", out var cl) ? cl.GetInt32() : 0;
        var maxResults = input.TryGetProperty("max_results", out var mr) ? mr.GetInt32() : 250;

        var args = new List<string> { "--line-number", "--no-heading", "--color=never" };

        if (caseInsensitive) args.Add("--ignore-case");
        if (contextLines > 0) args.AddRange(["--context", contextLines.ToString()]);
        if (glob is not null) args.AddRange(["--glob", glob]);
        args.AddRange(["--max-count", maxResults.ToString()]);
        args.Add("--");
        args.Add(pattern);
        args.Add(searchPath);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "rg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = context.WorkingDirectory,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            var process = Process.Start(psi);
            if (process is null)
                return ToolResult.Error("Failed to start ripgrep. Is it installed?");

            using (process)
            {
                var stdout = await process.StandardOutput.ReadToEndAsync(ct);
                var stderr = await process.StandardError.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);

                if (process.ExitCode == 1)
                    return ToolResult.Success($"No matches for pattern '{pattern}' in {searchPath}");

                if (process.ExitCode != 0)
                    return ToolResult.Error($"ripgrep error: {stderr.Trim()}");

                var lines = stdout.TrimEnd().Split('\n');
                var truncated = lines.Length > maxResults;
                var output = string.Join('\n', lines.Take(maxResults));

                var matches = ParseGrepOutput(lines.Take(maxResults), context.WorkingDirectory);
                var fileList = matches.Select(m => m.FilePath).Distinct().ToList();

                string? cursorId = null;
                if (context.Cursors is not null && matches.Count > 0)
                {
                    cursorId = context.Cursors.Store("Grep", new GrepCursorData(matches, fileList));
                }

                var header = truncated
                    ? $"Showing first {maxResults} matches (truncated):"
                    : $"Found {lines.Length} match(es) in {fileList.Count} file(s):";

                var cursorHint = cursorId is not null
                    ? $"\n(cursor: {cursorId} — use with FileRead's from_cursor to read matching files)"
                    : "";

                return ToolResult.SuccessWithPayload(
                    $"{header}{cursorHint}\n{output}",
                    new { matches, files = fileList, cursor_id = cursorId });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Error($"Grep error (is ripgrep installed?): {ex.Message}");
        }
    }

    private static List<GrepMatch> ParseGrepOutput(IEnumerable<string> lines, string workingDirectory)
    {
        var matches = new List<GrepMatch>();

        foreach (var line in lines)
        {

            var firstColon = line.IndexOf(':');
            if (firstColon <= 0) continue;

            var secondColon = line.IndexOf(':', firstColon + 1);
            if (secondColon <= firstColon) continue;

            var filePath = line[..firstColon];
            if (!int.TryParse(line[(firstColon + 1)..secondColon], out var lineNum))
                continue;

            var content = line[(secondColon + 1)..];

            var absolutePath = Path.IsPathRooted(filePath)
                ? filePath
                : Path.GetFullPath(filePath, workingDirectory);

            matches.Add(new GrepMatch(absolutePath, lineNum, content.Trim()));
        }

        return matches;
    }
}

public sealed record GrepCursorData(IReadOnlyList<GrepMatch> Matches, IReadOnlyList<string> Files);

public sealed record GrepMatch(string FilePath, int LineNumber, string Content);
