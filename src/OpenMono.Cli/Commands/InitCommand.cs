using OpenMono.Utils;

namespace OpenMono.Commands;

public sealed class InitCommand : ICommand
{
    public string Name => "init";
    public string Description => "Auto-generate OPENMONO.md by analyzing the current project";
    public CommandType Type => CommandType.Local;

    public async Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var cwd = context.WorkingDirectory;
        var targetPath = Path.Combine(cwd, "OPENMONO.md");

        if (File.Exists(targetPath))
        {
            context.Renderer.WriteWarning($"OPENMONO.md already exists at {targetPath}");
            var answer = await context.Renderer.AskUserAsync("Overwrite? [y/N]", ct);
            if (!answer.Equals("y", StringComparison.OrdinalIgnoreCase))
            {
                context.Renderer.WriteInfo("Cancelled.");
                return;
            }
        }

        context.Renderer.WriteInfo("Analyzing project structure...");

        var sections = new List<string> { "# Project Instructions\n" };

        var buildSection = await DetectBuildSystemAsync(cwd, ct);
        if (buildSection is not null)
            sections.Add(buildSection);

        var conventionSection = DetectConventions(cwd);
        if (conventionSection is not null)
            sections.Add(conventionSection);

        if (await GitHelper.IsGitRepoAsync(cwd, ct))
        {
            var branch = await GitHelper.GetCurrentBranchAsync(cwd, ct);
            sections.Add($"## Git\n- Default branch: {branch ?? "unknown"}");
        }

        sections.Add("""
            ## Do NOT
            - Modify files in vendor/ or node_modules/
            - Commit .env or credentials files
            - Add dependencies without justification
            """);

        var content = string.Join("\n\n", sections);
        await File.WriteAllTextAsync(targetPath, content, ct);

        context.Renderer.WriteInfo($"Created {targetPath}");
        context.Renderer.WriteMarkdown(content);
    }

    private static async Task<string?> DetectBuildSystemAsync(string cwd, CancellationToken ct)
    {
        var lines = new List<string> { "## Build" };

        var slnFiles = Directory.GetFiles(cwd, "*.sln", SearchOption.TopDirectoryOnly);
        var csprojFiles = Directory.GetFiles(cwd, "*.csproj", SearchOption.AllDirectories);
        if (slnFiles.Length > 0)
        {
            lines.Add($"- Solution: `dotnet build {Path.GetFileName(slnFiles[0])}`");
            lines.Add($"- Tests: `dotnet test`");
        }
        else if (csprojFiles.Length > 0)
        {
            lines.Add($"- Build: `dotnet build`");
            lines.Add($"- Tests: `dotnet test`");
        }

        if (File.Exists(Path.Combine(cwd, "package.json")))
        {
            var hasYarn = File.Exists(Path.Combine(cwd, "yarn.lock"));
            var hasPnpm = File.Exists(Path.Combine(cwd, "pnpm-lock.yaml"));
            var pm = hasPnpm ? "pnpm" : hasYarn ? "yarn" : "npm";
            lines.Add($"- Install: `{pm} install`");
            lines.Add($"- Build: `{pm} run build`");
            lines.Add($"- Test: `{pm} test`");
        }

        if (File.Exists(Path.Combine(cwd, "pyproject.toml")) ||
            File.Exists(Path.Combine(cwd, "setup.py")))
        {
            var hasPoetry = File.Exists(Path.Combine(cwd, "poetry.lock"));
            if (hasPoetry)
            {
                lines.Add("- Install: `poetry install`");
                lines.Add("- Test: `poetry run pytest`");
            }
            else
            {
                lines.Add("- Install: `pip install -e .`");
                lines.Add("- Test: `pytest`");
            }
        }

        if (File.Exists(Path.Combine(cwd, "go.mod")))
        {
            lines.Add("- Build: `go build ./...`");
            lines.Add("- Test: `go test ./...`");
        }

        if (File.Exists(Path.Combine(cwd, "Cargo.toml")))
        {
            lines.Add("- Build: `cargo build`");
            lines.Add("- Test: `cargo test`");
        }

        if (File.Exists(Path.Combine(cwd, "Makefile")))
        {
            var (exit, stdout, _) = await ProcessRunner.RunAsync(
                "head -20 Makefile | grep -E '^[a-zA-Z_-]+:' | head -5 | sed 's/:.*//'",
                cwd, ct: ct);
            if (exit == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                var targets = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                lines.Add($"- Makefile targets: {string.Join(", ", targets.Select(t => $"`make {t.Trim()}`"))}");
            }
        }

        if (File.Exists(Path.Combine(cwd, "Dockerfile")) ||
            File.Exists(Path.Combine(cwd, "docker-compose.yml")) ||
            File.Exists(Path.Combine(cwd, "docker-compose.yaml")))
        {
            lines.Add("- Docker: `docker compose up`");
        }

        return lines.Count > 1 ? string.Join('\n', lines) : null;
    }

    private static string? DetectConventions(string cwd)
    {
        var lines = new List<string> { "## Conventions" };

        var langCounts = new Dictionary<string, int>();
        foreach (var ext in new[] { "*.cs", "*.ts", "*.tsx", "*.js", "*.py", "*.go", "*.rs", "*.java" })
        {
            var count = Directory.GetFiles(cwd, ext, SearchOption.AllDirectories).Length;
            if (count > 0) langCounts[ext.TrimStart('*', '.')] = count;
        }

        if (langCounts.Count > 0)
        {
            var primary = langCounts.OrderByDescending(kv => kv.Value).First();
            lines.Add($"- Primary language: {primary.Key} ({primary.Value} files)");
        }

        if (File.Exists(Path.Combine(cwd, ".editorconfig")))
            lines.Add("- Code style: .editorconfig present — follow its rules");
        if (File.Exists(Path.Combine(cwd, ".prettierrc")) ||
            File.Exists(Path.Combine(cwd, ".prettierrc.json")))
            lines.Add("- Formatting: Prettier configured");
        if (File.Exists(Path.Combine(cwd, ".eslintrc.json")) ||
            File.Exists(Path.Combine(cwd, ".eslintrc.js")) ||
            File.Exists(Path.Combine(cwd, "eslint.config.js")))
            lines.Add("- Linting: ESLint configured");

        return lines.Count > 1 ? string.Join('\n', lines) : null;
    }
}
