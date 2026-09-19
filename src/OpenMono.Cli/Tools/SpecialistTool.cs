using System.Diagnostics;
using System.Text.Json;
using OpenMono.Decisions;
using OpenMono.Utils;

namespace OpenMono.Tools;

public sealed class SpecialistTool : ToolBase
{
    private static readonly string[] SkippedDirs =
    [".git", "bin", "obj", "node_modules", ".openmono", ".vs", "TestResults"];

    private static readonly string[] CodeExtensions =
    [".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs", ".java", ".rb", ".php", ".swift", ".kt"];

    private static readonly string[] DocExtensions = [".md", ".txt", ".rst", ".mdx"];

    private static readonly string[] FixMarkers = ["TODO", "FIXME", "XXX", "HACK", "BUG"];

    private readonly DecisionOptions _options;
    private readonly string _workingDirectory;

    public SpecialistTool(DecisionOptions options, string workingDirectory)
    {
        _options = options;
        _workingDirectory = workingDirectory;
    }

    public override string Name => "specialist";

    public override string Description =>
        "Codebase triage. Tasks: triage-files routes files to fix, test, docs, or skip; commit-ready routes to stage, test, or skip. Dry-run report only.";

    public override bool IsReadOnly => true;

    public override bool IsConcurrencySafe => true;

    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddEnum("task", "Specialist task", "triage-files", "commit-ready")
        .AddString("scope", "Path, directory, or glob under the workspace. Empty means the whole workspace.")
        .AddProperty("min_confidence", new { type = "number", description = "Minimum confidence kept" })
        .AddInteger("max_steps", "Maximum files classified", minimum: 1, maximum: 256)
        .Require("task");

    protected override Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var task = input.TryGetProperty("task", out var t) ? t.GetString() ?? "" : "";
        if (task != "triage-files" && task != "commit-ready")
            return Task.FromResult(ToolResult.InvalidInput($"Unknown task '{task}'.", "Use triage-files or commit-ready."));
        var scope = input.TryGetProperty("scope", out var s) ? s.GetString() ?? "" : "";
        var minConfidence = _options.MinConfidence;
        if (input.TryGetProperty("min_confidence", out var minEl) && minEl.ValueKind == JsonValueKind.Number)
            minConfidence = Math.Clamp(minEl.GetDouble(), 0, 1);
        var maxSteps = _options.MaxSteps;
        if (input.TryGetProperty("max_steps", out var maxEl) && maxEl.ValueKind == JsonValueKind.Number)
            maxSteps = Math.Clamp(maxEl.GetInt32(), 1, 256);
        var sw = Stopwatch.StartNew();
        var files = EnumerateScope(scope).Take(maxSteps).ToList();
        var enumerateMs = sw.ElapsedMilliseconds;
        var items = new List<DecisionItem>(files.Count);
        var warnings = new List<string>();
        for (var idx = 0; idx < files.Count; idx++)
        {
            var relative = Path.GetRelativePath(_workingDirectory, files[idx]);
            var (action, confidence) = task == "triage-files"
                ? TriageFile(files[idx], warnings)
                : CommitFile(files[idx], warnings);
            items.Add(new DecisionItem("file", relative, action, confidence, idx, relative));
            ct.ThrowIfCancellationRequested();
        }
        var vocabulary = task == "triage-files"
            ? new HashSet<string>(StringComparer.Ordinal) { "fix", "test", "docs", "skip" }
            : new HashSet<string>(StringComparer.Ordinal) { "stage", "test", "skip" };
        var (valid, errors) = DecisionValidator.Validate(items, vocabulary, Math.Max(files.Count - 1, 0));
        foreach (var error in errors)
            warnings.Add(error);
        var (ordered, skipped) = DecisionOrderer.Order(valid, minConfidence, false);
        sw.Stop();
        var report = new DecisionReport(
            task, true, "execute:false submit:false", valid, ordered, skipped,
            new Dictionary<string, long> { ["enumerate"] = enumerateMs, ["score"] = sw.ElapsedMilliseconds },
            warnings);
        return Task.FromResult(ToolResult.SuccessWithPayload(report.ToRedactedMarkdown(), report));
    }

    private (string Action, double Confidence) TriageFile(string path, List<string> warnings)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name.Contains("test", StringComparison.Ordinal) || name.Contains("spec", StringComparison.Ordinal))
            return ("test", 0.9);
        if (DocExtensions.Contains(Path.GetExtension(path).ToLowerInvariant(), StringComparer.Ordinal))
            return ("docs", 0.9);
        var content = ReadHead(path, warnings);
        if (content is null)
            return ("skip", 0.5);
        if (FixMarkers.Any(m => content.Contains(m, StringComparison.Ordinal)))
            return ("fix", 0.9);
        if (CodeExtensions.Contains(Path.GetExtension(path).ToLowerInvariant(), StringComparer.Ordinal))
            return ("fix", 0.75);
        return ("skip", 0.5);
    }

    private (string Action, double Confidence) CommitFile(string path, List<string> warnings)
    {
        var content = ReadHead(path, warnings);
        if (content is null)
            return ("skip", 0.5);
        if (SecretScanner.Scan(content).Count > 0)
            return ("skip", 0.95);
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name.Contains("test", StringComparison.Ordinal) || name.Contains("spec", StringComparison.Ordinal))
            return ("test", 0.9);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (CodeExtensions.Contains(ext, StringComparer.Ordinal) || DocExtensions.Contains(ext, StringComparer.Ordinal))
            return ("stage", 0.75);
        return ("skip", 0.5);
    }

    private static string? ReadHead(string path, List<string> warnings)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[4096];
            var read = stream.Read(buffer, 0, buffer.Length);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"unreadable:{path}");
            return null;
        }
    }

    private IReadOnlyList<string> EnumerateScope(string scope)
    {
        string directory;
        string pattern;
        bool recursive;
        if (string.IsNullOrWhiteSpace(scope))
        {
            directory = _workingDirectory;
            pattern = "*";
            recursive = true;
        }
        else if (scope.EndsWith("/**", StringComparison.Ordinal))
        {
            directory = Path.GetFullPath(scope[..^3], _workingDirectory);
            pattern = "*";
            recursive = true;
        }
        else if (scope.Contains('*'))
        {
            directory = Path.GetFullPath(Path.GetDirectoryName(scope) ?? ".", _workingDirectory);
            pattern = Path.GetFileName(scope);
            recursive = false;
        }
        else
        {
            var full = Path.GetFullPath(scope, _workingDirectory);
            if (File.Exists(full))
                return [full];
            directory = full;
            pattern = "*";
            recursive = true;
        }
        if (!Directory.Exists(directory))
            return [];
        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        return Directory.EnumerateFiles(directory, pattern, option)
            .Where(p => !IsSkipped(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsSkipped(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar);
        return parts.Any(p => SkippedDirs.Contains(p, StringComparer.OrdinalIgnoreCase));
    }
}
