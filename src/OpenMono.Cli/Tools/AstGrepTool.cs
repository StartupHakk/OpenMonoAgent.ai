using System.Diagnostics;
using System.Text.Json;
using OpenMono.Permissions;

namespace OpenMono.Tools;

public sealed class AstGrepTool : ToolBase
{
    public override string Name => "AstGrep";
    public override string Description =>
        "Structural code search using ast-grep (sg). Search for code patterns using " +
        "metavariables ($X, $FN, $ARG, etc.) instead of literal text. Supports Rust, " +
        "TypeScript, Python, Go, C#, and more. Use this when Grep can't match " +
        "structural patterns like 'all functions returning Result' or 'all .unwrap() calls'. " +
        "Examples: pattern='fn $FN() -> Result<_, $E>' finds all functions returning Result; " +
        "pattern='$X.unwrap()' finds all unwrap calls; pattern='match $E { Ok($V) => $A, _ => $B }' " +
        "finds match arms with Ok patterns.";
    public override bool IsConcurrencySafe => true;
    public override bool IsReadOnly => true;
    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;
    public override TimeSpan? Timeout => TimeSpan.FromSeconds(60);

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("pattern", "ast-grep pattern with metavars (e.g. 'fn $FN() -> Result<_, $E>', '$X.unwrap()', 'if $COND { $BODY }')")
        .AddString("path", "File or directory to search in (default: working directory)")
        .AddEnum("language", "Language for pattern matching", "rust", "typescript", "python", "go", "csharp", "javascript", "html", "css", "json")
        .AddString("rewrite", "Optional: replacement pattern. When set, performs a rewrite instead of search (e.g. '$X.expect($MSG)' -> '$X.context($MSG)'). Requires strict=true for in-place edits.")
        .AddBoolean("strict", "When true with rewrite, applies edits to files in-place. Without rewrite, strict mode only matches exact patterns (no partial matches).")
        .AddInteger("max_results", "Maximum matches to return (default: 100)")
        .Require("pattern");

    public override IReadOnlyList<Capability> RequiredCapabilities(JsonElement input)
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
        var language = input.TryGetProperty("language", out var l) ? l.GetString() : "rust";
        var rewrite = input.TryGetProperty("rewrite", out var rw) ? rw.GetString() : null;
        var strict = input.TryGetProperty("strict", out var s) && s.GetBoolean();
        var maxResults = input.TryGetProperty("max_results", out var mr) ? mr.GetInt32() : 100;

        if (!Directory.Exists(searchPath) && !File.Exists(searchPath))
            return ToolResult.Error($"Path not found: {searchPath}");

        var sgPath = FindBinary("sg") ?? FindBinary("ast-grep");
        if (sgPath is null)
            return ToolResult.Error(
                "ast-grep (sg) not found. Install: cargo install ast-grep, npm install -g @ast-grep/cli, or brew install ast-grep");

        var args = new List<string> { "run" };

        args.AddRange(["-p", pattern]);
        if (!string.IsNullOrEmpty(language))
            args.AddRange(["-l", language]);

        if (!string.IsNullOrEmpty(rewrite))
        {
            args.AddRange(["-r", rewrite]);
            if (strict)
                args.Add("--update");
        }

        args.Add("--json=compact");
        args.Add("--");
        args.Add(searchPath);

        try
        {
            var (stdout, stderr, exitCode) = await RunProcessAsync(sgPath, args, context.WorkingDirectory, ct);

            if (exitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            {
                if (stderr.Contains("No match") || stderr.Contains("no matches"))
                    return ToolResult.Success($"No matches for pattern '{pattern}'");
                return ToolResult.Error($"ast-grep error: {stderr.Trim()}");
            }

            if (string.IsNullOrWhiteSpace(stdout))
                return ToolResult.Success($"No matches for pattern '{pattern}' in {searchPath}");

            List<AstGrepMatch> matches;
            try
            {
                matches = ParseJsonOutput(stdout, context.WorkingDirectory);
            }
            catch
            {
                return ToolResult.Success($"ast-grep output (raw):\n{stdout[..Math.Min(stdout.Length, 2000)]}");
            }

            if (matches.Count == 0)
                return ToolResult.Success($"No matches for pattern '{pattern}'");

            var truncated = matches.Count > maxResults;
            var display = matches.Take(maxResults).ToList();

            var files = display.Select(m => m.FilePath).Distinct().ToList();
            var header = truncated
                ? $"Found {matches.Count} matches in {files.Count}+ files (showing first {maxResults}):"
                : $"Found {matches.Count} match(es) in {files.Count} file(s):";

            var details = display.Select(m =>
                $"  {Path.GetRelativePath(context.WorkingDirectory, m.FilePath)}:{m.Line}:{m.Column} — {m.Text.Trim()}");

            var payload = new { matches = display, files, total = matches.Count };

            var rewriteNote = !string.IsNullOrEmpty(rewrite)
                ? (strict ? "\n(rewrite applied in-place)" : "\n(rewrite preview only — set strict=true to apply)")
                : "";

            return ToolResult.SuccessWithPayload(
                $"{header}{rewriteNote}\n{string.Join('\n', details)}",
                payload);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResult.Error($"AstGrep error: {ex.Message}");
        }
    }

    private static string? FindBinary(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin";
        foreach (var dir in path.Split(':'))
        {
            var fullPath = Path.Combine(dir, name);
            if (File.Exists(fullPath) && IsExecutable(fullPath))
                return fullPath;
        }
        return null;
    }

    private static bool IsExecutable(string path)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                ArgumentList = { "--version" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
            return p?.ExitCode == 0 || p?.ExitCode == 1;
        }
        catch { return false; }
    }

    private static List<AstGrepMatch> ParseJsonOutput(string json, string workingDir)
    {
        var matches = new List<AstGrepMatch>();

        using var doc = JsonDocument.Parse(json);
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var file = element.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "";
            var line = element.TryGetProperty("range", out var range) &&
                       range.TryGetProperty("start", out var start) &&
                       start.TryGetProperty("line", out var ln)
                ? ln.GetInt32()
                : 0;
            var column = element.TryGetProperty("range", out var range2) &&
                         range2.TryGetProperty("start", out var start2) &&
                         start2.TryGetProperty("column", out var col)
                ? col.GetInt32()
                : 0;
            var text = element.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";

            var absolutePath = Path.IsPathRooted(file)
                ? file
                : Path.GetFullPath(file, workingDir);

            matches.Add(new AstGrepMatch(absolutePath, line + 1, column, text));
        }

        return matches;
    }

    private static async Task<(string stdout, string stderr, int exitCode)> RunProcessAsync(
        string fileName, List<string> args, string workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start {fileName}");

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (stdout, stderr, process.ExitCode);
    }
}

public sealed record AstGrepMatch(string FilePath, int Line, int Column, string Text);
