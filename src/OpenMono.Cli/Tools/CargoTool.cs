using System.Diagnostics;
using System.Text.Json;
using OpenMono.Permissions;

namespace OpenMono.Tools;

public sealed class CargoTool : ToolBase
{
    public override string Name => "Cargo";
    public override string Description =>
        "Run cargo commands for Rust projects with structured output. " +
        "Actions: 'check' (compile check), 'clippy' (lint), 'test' (run tests), " +
        "'build' (compile), 'fmt' (format check), 'tree' (dependency tree), " +
        "'metadata' (workspace info), 'audit' (security vulnerabilities). " +
        "Returns structured diagnostics (file, line, severity, message, lint code) " +
        "instead of raw stdout. For check/clippy, uses --message-format=json to " +
        "parse compiler messages. For test, parses test results into pass/fail counts.";
    public override bool IsConcurrencySafe => false;
    public override bool IsReadOnly => true;
    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;
    public override TimeSpan? Timeout => TimeSpan.FromSeconds(300);

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddEnum("action", "Cargo action to run", "check", "clippy", "test", "build", "fmt", "tree", "metadata", "audit")
        .AddString("package", "Specific package to check (e.g. 'my-crate'). Uses -p <name>.")
        .AddString("manifest_path", "Path to Cargo.toml OR directory containing it (default: auto-detect in working directory)")
        .AddArray("extra_args", "Additional cargo arguments (e.g. ['--all-features', '--release'])", new { type = "string" })
        .Require("action");

    public override IReadOnlyList<Capability> RequiredCapabilities(JsonElement input)
    {
        var action = input.TryGetProperty("action", out var a) ? a.GetString() : "check";
        return action is "test" or "build" or "clippy" or "audit"
            ? [new ProcessExecCap("cargo", [action ?? "check"])]
            : [];
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var action = input.GetProperty("action").GetString()!;
        var package = input.TryGetProperty("package", out var p) ? p.GetString() : null;
        var manifestPath = input.TryGetProperty("manifest_path", out var mp) ? mp.GetString() : null;
        var extraArgs = new List<string>();
        if (input.TryGetProperty("extra_args", out var ea) && ea.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in ea.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String)
                    extraArgs.Add(item.GetString()!);
        }

        var cargoPath = FindCargo();
        if (cargoPath is null)
            return ToolResult.Error("cargo not found. Is Rust installed?");

        var workingDir = context.WorkingDirectory;
        if (!string.IsNullOrEmpty(manifestPath))
        {
            // manifest_path can be either a Cargo.toml file or a directory containing one
            if (Directory.Exists(manifestPath))
            {
                var found = FindCargoToml(manifestPath);
                workingDir = found is not null
                    ? Path.GetDirectoryName(found) ?? manifestPath
                    : manifestPath;
            }
            else if (File.Exists(manifestPath))
            {
                workingDir = Path.GetDirectoryName(manifestPath) ?? workingDir;
            }
            else
            {
                // Treat as a directory path that might not exist yet
                workingDir = manifestPath;
            }
        }
        else
        {
            var foundManifest = FindCargoToml(workingDir);
            if (foundManifest is null)
                return ToolResult.Error($"No Cargo.toml found in {workingDir} or parent directories");
            workingDir = Path.GetDirectoryName(foundManifest) ?? workingDir;
        }

        return action switch
        {
            "check" => await RunCargoWithDiagnosticsAsync(cargoPath, "check", package, extraArgs, workingDir, ct),
            "clippy" => await RunCargoWithDiagnosticsAsync(cargoPath, "clippy", package, extraArgs, workingDir, ct),
            "test" => await RunCargoTestAsync(cargoPath, package, extraArgs, workingDir, ct),
            "build" => await RunCargoBuildAsync(cargoPath, package, extraArgs, workingDir, ct),
            "fmt" => await RunCargoFmtAsync(cargoPath, extraArgs, workingDir, ct),
            "tree" => await RunCargoTreeAsync(cargoPath, package, workingDir, ct),
            "metadata" => await RunCargoMetadataAsync(cargoPath, workingDir, ct),
            "audit" => await RunCargoAuditAsync(workingDir, ct),
            _ => ToolResult.Error($"Unknown cargo action: {action}. Use: check, clippy, test, build, fmt, tree, metadata, audit")
        };
    }

    private static async Task<ToolResult> RunCargoWithDiagnosticsAsync(
        string cargoPath, string subcommand, string? package, List<string> extraArgs,
        string workingDir, CancellationToken ct)
    {
        var args = new List<string> { subcommand, "--message-format=json" };
        if (!string.IsNullOrEmpty(package))
            args.AddRange(["-p", package]);
        args.AddRange(extraArgs);

        var (stdout, stderr, exitCode) = await RunProcessAsync(cargoPath, args, workingDir, ct);

        var diagnostics = ParseCargoDiagnostics(stdout);
        var errors = diagnostics.Where(d => d.Severity == "error").ToList();
        var warnings = diagnostics.Where(d => d.Severity == "warning").ToList();

        var summary = exitCode == 0
            ? $"{subcommand} succeeded — {warnings.Count} warning(s)"
            : $"{subcommand} FAILED — {errors.Count} error(s), {warnings.Count} warning(s)";

        var detail = diagnostics
            .Where(d => d.Severity == "error" || d.Severity == "warning")
            .Take(50)
            .Select(d => $"  [{d.Severity}] {d.File}:{d.Line}:{d.Column} {d.Code ?? ""} — {d.Message}");

        var payload = new
        {
            success = exitCode == 0,
            errors = errors,
            warnings = warnings,
            total_diagnostics = diagnostics.Count,
        };

        var output = $"{summary}\n{string.Join('\n', detail)}";
        if (stderr.Trim().Length > 0 && exitCode != 0 && diagnostics.Count == 0)
            output += $"\n\nRaw stderr:\n{stderr[..Math.Min(stderr.Length, 2000)]}";

        return exitCode == 0
            ? ToolResult.SuccessWithPayload(output, payload)
            : ToolResult.SuccessWithPayload(output, payload);
    }

    private static async Task<ToolResult> RunCargoTestAsync(
        string cargoPath, string? package, List<string> extraArgs,
        string workingDir, CancellationToken ct)
    {
        var args = new List<string> { "test", "--message-format=short" };
        if (!string.IsNullOrEmpty(package))
            args.AddRange(["-p", package]);
        args.AddRange(extraArgs);

        var (stdout, stderr, exitCode) = await RunProcessAsync(cargoPath, args, workingDir, ct);

        var testResults = ParseTestResults(stdout);
        var summary = exitCode == 0
            ? $"Tests passed — {testResults.Passed} passed, {testResults.Failed} failed, {testResults.Ignored} ignored"
            : $"Tests FAILED — {testResults.Passed} passed, {testResults.Failed} failed, {testResults.Ignored} ignored";

        var failedTests = testResults.Failures
            .Take(20)
            .Select(f => $"  FAIL: {f}");

        var payload = new
        {
            success = exitCode == 0,
            passed = testResults.Passed,
            failed = testResults.Failed,
            ignored = testResults.Ignored,
            failures = testResults.Failures,
        };

        var output = $"{summary}\n{string.Join('\n', failedTests)}";
        if (testResults.Failures.Count == 0 && stdout.Length > 0)
            output += $"\n\n{stdout[..Math.Min(stdout.Length, 2000)]}";

        return ToolResult.SuccessWithPayload(output, payload);
    }

    private static async Task<ToolResult> RunCargoBuildAsync(
        string cargoPath, string? package, List<string> extraArgs,
        string workingDir, CancellationToken ct)
    {
        var args = new List<string> { "build", "--message-format=short" };
        if (!string.IsNullOrEmpty(package))
            args.AddRange(["-p", package]);
        args.AddRange(extraArgs);

        var (stdout, stderr, exitCode) = await RunProcessAsync(cargoPath, args, workingDir, ct);

        var summary = exitCode == 0
            ? "Build succeeded"
            : "Build FAILED";

        var output = $"{summary}\n{stdout[..Math.Min(stdout.Length, 3000)]}";
        if (!string.IsNullOrWhiteSpace(stderr) && exitCode != 0)
            output += $"\n\n{stderr[..Math.Min(stderr.Length, 2000)]}";

        return ToolResult.SuccessWithPayload(output, new { success = exitCode == 0 });
    }

    private static async Task<ToolResult> RunCargoFmtAsync(
        string cargoPath, List<string> extraArgs, string workingDir, CancellationToken ct)
    {
        var args = new List<string> { "fmt", "--", "--check" };
        args.AddRange(extraArgs);

        var (stdout, stderr, exitCode) = await RunProcessAsync(cargoPath, args, workingDir, ct);

        if (exitCode == 0)
            return ToolResult.Success("Formatting OK — no changes needed");

        var diffFiles = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("Diff in "))
            .Select(l => l.Replace("Diff in ", "").Trim())
            .ToList();

        return ToolResult.SuccessWithPayload(
            $"Formatting issues in {diffFiles.Count} file(s):\n{string.Join('\n', diffFiles.Select(f => $"  {f}"))}",
            new { needs_formatting = diffFiles });
    }

    private static async Task<ToolResult> RunCargoTreeAsync(
        string cargoPath, string? package, string workingDir, CancellationToken ct)
    {
        var args = new List<string> { "tree" };
        if (!string.IsNullOrEmpty(package))
            args.AddRange(["-p", package]);

        var (stdout, _, exitCode) = await RunProcessAsync(cargoPath, args, workingDir, ct);

        return exitCode == 0
            ? ToolResult.Success(stdout[..Math.Min(stdout.Length, 5000)])
            : ToolResult.Error("cargo tree failed");
    }

    private static async Task<ToolResult> RunCargoMetadataAsync(
        string cargoPath, string workingDir, CancellationToken ct)
    {
        var args = new List<string> { "metadata", "--format-version=1", "--no-deps" };

        var (stdout, _, exitCode) = await RunProcessAsync(cargoPath, args, workingDir, ct);

        if (exitCode != 0)
            return ToolResult.Error("cargo metadata failed");

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var packages = doc.RootElement.GetProperty("packages");
            var workspaceMembers = doc.RootElement.GetProperty("workspace_members");

            var packageList = packages.EnumerateArray().Select(p => new
            {
                name = p.GetProperty("name").GetString(),
                version = p.GetProperty("version").GetString(),
                manifest_path = p.GetProperty("manifest_path").GetString(),
            }).ToList();

            var summary = $"Workspace: {packageList.Count} package(s)\n" +
                         string.Join('\n', packageList.Select(p => $"  {p.name} v{p.version}"));

            return ToolResult.SuccessWithPayload(summary, new { packages = packageList });
        }
        catch
        {
            return ToolResult.Success(stdout[..Math.Min(stdout.Length, 3000)]);
        }
    }

    private static async Task<ToolResult> RunCargoAuditAsync(string workingDir, CancellationToken ct)
    {
        var auditPath = FindBinary("cargo-audit") ?? FindBinaryInHome("cargo-audit");
        if (auditPath is null)
            return ToolResult.Error("cargo-audit not installed. Run: cargo install cargo-audit");

        var (stdout, stderr, exitCode) = await RunProcessAsync(
            auditPath, ["audit", "--json"], workingDir, ct);

        if (exitCode == 0)
            return ToolResult.Success("No vulnerabilities found");

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var vulnerabilities = new List<object>();
            if (doc.RootElement.TryGetProperty("vulnerabilities", out var vulns))
            {
                foreach (var v in vulns.EnumerateArray())
                {
                    vulnerabilities.Add(new
                    {
                        advisory_id = v.TryGetProperty("advisory", out var adv) && adv.TryGetProperty("id", out var id) ? id.GetString() : "",
                        package = v.TryGetProperty("package", out var pkg) && pkg.TryGetProperty("name", out var name) ? name.GetString() : "",
                        severity = v.TryGetProperty("advisory", out var adv2) && adv2.TryGetProperty("severity", out var sev) ? sev.GetString() : "",
                        title = v.TryGetProperty("advisory", out var adv3) && adv3.TryGetProperty("title", out var title) ? title.GetString() : "",
                    });
                }
            }
            var summary = $"Found {vulnerabilities.Count} vulnerability(ies):\n" +
                          string.Join('\n', vulnerabilities.Select(v =>
                          {
                              dynamic d = v;
                              return $"  [{d.severity}] {d.package} — {d.title} ({d.advisory_id})";
                          }));
            return ToolResult.SuccessWithPayload(summary, new { vulnerabilities });
        }
        catch
        {
            return ToolResult.Success(stdout[..Math.Min(stdout.Length, 3000)]);
        }
    }

    private static List<CargoDiagnostic> ParseCargoDiagnostics(string jsonOutput)
    {
        var diagnostics = new List<CargoDiagnostic>();
        var lines = jsonOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (!root.TryGetProperty("reason", out var reason))
                    continue;

                var reasonStr = reason.GetString();
                if (reasonStr is not ("compiler-message" or "compiler-artifact"))
                    continue;

                if (!root.TryGetProperty("message", out var msg))
                    continue;

                var severity = msg.TryGetProperty("level", out var lvl) ? lvl.GetString() ?? "info" : "info";
                var message = msg.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                var code = msg.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Object
                    && codeEl.TryGetProperty("code", out var codeStr) ? codeStr.GetString() : null;

                var file = "";
                var lineNum = 0;
                var col = 0;

                if (msg.TryGetProperty("spans", out var spans) && spans.ValueKind == JsonValueKind.Array)
                {
                    var primarySpan = spans.EnumerateArray().FirstOrDefault(s =>
                        s.TryGetProperty("is_primary", out var prim) && prim.GetBoolean());
                    if (primarySpan.TryGetProperty("file_name", out var fn))
                        file = fn.GetString() ?? "";
                    if (primarySpan.TryGetProperty("line_start", out var ln))
                        lineNum = ln.GetInt32();
                    if (primarySpan.TryGetProperty("column_start", out var c))
                        col = c.GetInt32();
                }

                diagnostics.Add(new CargoDiagnostic(severity, file, lineNum, col, message, code));
            }
            catch { }
        }

        return diagnostics;
    }

    private static TestResults ParseTestResults(string output)
    {
        var results = new TestResults();
        var lines = output.Split('\n');

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("test ") && trimmed.Contains("... "))
            {
                if (trimmed.EndsWith("ok"))
                    results.Passed++;
                else if (trimmed.EndsWith("FAILED") || trimmed.Contains("FAILED"))
                {
                    results.Failed++;
                    var name = trimmed.Substring(5, trimmed.IndexOf("... ") - 5);
                    results.Failures.Add(name.Trim());
                }
                else if (trimmed.EndsWith("ignored"))
                    results.Ignored++;
            }
        }

        return results;
    }

    private static string? FindCargo()
    {
        return FindBinary("cargo");
    }

    private static string? FindBinary(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin";
        foreach (var dir in path.Split(':'))
        {
            var fullPath = Path.Combine(dir, name);
            if (File.Exists(fullPath))
                return fullPath;
            var exePath = fullPath + ".exe";
            if (File.Exists(exePath))
                return exePath;
        }
        return null;
    }

    private static string? FindBinaryInHome(string name)
    {
        var home = Environment.GetEnvironmentVariable("HOME") ?? "/root";
        var cargoBin = Path.Combine(home, ".cargo", "bin", name);
        return File.Exists(cargoBin) ? cargoBin : null;
    }

    private static string? FindCargoToml(string dir)
    {
        var current = new DirectoryInfo(dir);
        while (current is not null)
        {
            var manifest = Path.Combine(current.FullName, "Cargo.toml");
            if (File.Exists(manifest))
                return manifest;
            current = current.Parent;
        }
        return null;
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

public sealed record CargoDiagnostic(
    string Severity,
    string File,
    int Line,
    int Column,
    string Message,
    string? Code);

public sealed class TestResults
{
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Ignored { get; set; }
    public List<string> Failures { get; set; } = [];
}
