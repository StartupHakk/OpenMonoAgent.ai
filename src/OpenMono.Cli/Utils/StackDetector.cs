namespace OpenMono.Utils;

public sealed record StackCommand(string Label, string Command);

public sealed record DetectedStack(string Name, IReadOnlyList<StackCommand> Commands);

public static class StackDetector
{
    public static IReadOnlyList<DetectedStack> Detect(string cwd)
    {
        var stacks = new List<DetectedStack>();

        var slnFiles = SafeGetFiles(cwd, "*.sln", SearchOption.TopDirectoryOnly);
        var csprojFiles = SafeGetFiles(cwd, "*.csproj", SearchOption.AllDirectories);
        if (slnFiles.Count > 0)
        {
            stacks.Add(new DetectedStack(".NET", new[]
            {
                new StackCommand("Solution", $"dotnet build {Path.GetFileName(slnFiles[0])}"),
                new StackCommand("Test", "dotnet test"),
                new StackCommand("Run", "dotnet run"),
                new StackCommand("Add dependency", "dotnet add package <name>"),
            }));
        }
        else if (csprojFiles.Count > 0)
        {
            stacks.Add(new DetectedStack(".NET", new[]
            {
                new StackCommand("Build", "dotnet build"),
                new StackCommand("Test", "dotnet test"),
                new StackCommand("Run", "dotnet run"),
                new StackCommand("Add dependency", "dotnet add package <name>"),
            }));
        }

        if (File.Exists(Path.Combine(cwd, "package.json")))
        {
            var pm = File.Exists(Path.Combine(cwd, "pnpm-lock.yaml")) ? "pnpm"
                : File.Exists(Path.Combine(cwd, "yarn.lock")) ? "yarn"
                : "npm";
            stacks.Add(new DetectedStack("Node.js", new[]
            {
                new StackCommand("Install", $"{pm} install"),
                new StackCommand("Build", $"{pm} run build"),
                new StackCommand("Test", $"{pm} test"),
                new StackCommand("Run", pm == "npm" ? "npm start" : $"{pm} start"),
                new StackCommand("Add dependency", pm == "npm" ? "npm install <name>" : $"{pm} add <name>"),
            }));
        }

        if (File.Exists(Path.Combine(cwd, "pyproject.toml")) ||
            File.Exists(Path.Combine(cwd, "setup.py")))
        {
            if (File.Exists(Path.Combine(cwd, "poetry.lock")))
            {
                stacks.Add(new DetectedStack("Python", new[]
                {
                    new StackCommand("Install", "poetry install"),
                    new StackCommand("Test", "poetry run pytest"),
                    new StackCommand("Add dependency", "poetry add <name>"),
                }));
            }
            else
            {
                stacks.Add(new DetectedStack("Python", new[]
                {
                    new StackCommand("Install", "pip install -e ."),
                    new StackCommand("Test", "pytest"),
                    new StackCommand("Add dependency", "pip install <name>"),
                }));
            }
        }

        if (File.Exists(Path.Combine(cwd, "go.mod")))
        {
            stacks.Add(new DetectedStack("Go", new[]
            {
                new StackCommand("Build", "go build ./..."),
                new StackCommand("Test", "go test ./..."),
                new StackCommand("Run", "go run ."),
                new StackCommand("Add dependency", "go get <name>"),
            }));
        }

        if (File.Exists(Path.Combine(cwd, "Cargo.toml")))
        {
            stacks.Add(new DetectedStack("Rust", new[]
            {
                new StackCommand("Build", "cargo build"),
                new StackCommand("Test", "cargo test"),
                new StackCommand("Run", "cargo run"),
                new StackCommand("Add dependency", "cargo add <name>"),
            }));
        }

        if (File.Exists(Path.Combine(cwd, "pom.xml")))
        {
            stacks.Add(new DetectedStack("Java (Maven)", new[]
            {
                new StackCommand("Build", "mvn compile"),
                new StackCommand("Test", "mvn test"),
                new StackCommand("Package", "mvn package"),
            }));
        }
        else if (File.Exists(Path.Combine(cwd, "build.gradle")) ||
                 File.Exists(Path.Combine(cwd, "build.gradle.kts")))
        {
            var gradle = File.Exists(Path.Combine(cwd, "gradlew")) ? "./gradlew" : "gradle";
            stacks.Add(new DetectedStack("Java (Gradle)", new[]
            {
                new StackCommand("Build", $"{gradle} build"),
                new StackCommand("Test", $"{gradle} test"),
            }));
        }

        return stacks;
    }

    public static string BuildPromptSection(IReadOnlyList<DetectedStack> stacks)
    {
        if (stacks.Count == 0)
        {
            return """
                # Project Stack

                No standard build system was auto-detected. Inspect the project files to determine
                the language and toolchain, then use that stack's own build, test, and run commands.
                Do not assume any particular stack (do not default to `dotnet`).
                """;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Project Stack (auto-detected)");
        sb.AppendLine();
        sb.AppendLine("The stack(s) below were detected from files already in the working directory.");
        sb.AppendLine("For work on the EXISTING code here, prefer these commands to build, test, run,");
        sb.AppendLine("and add dependencies.");
        sb.AppendLine();
        sb.AppendLine("This is context, NOT a restriction on what you may build. If the user asks you");
        sb.AppendLine("to create, scaffold, or add a project in a different stack (e.g. a Next.js app");
        sb.AppendLine("with `npx create-next-app`, `cargo new`, `django-admin startproject`), use that");
        sb.AppendLine("stack's own tooling freely — you are a full-stack agent and work in whatever");
        sb.AppendLine("language and framework the user asks for, regardless of what is detected here.");
        sb.AppendLine();
        foreach (var stack in stacks)
        {
            var cmds = string.Join(", ", stack.Commands.Select(c => $"{c.Label}: `{c.Command}`"));
            sb.AppendLine($"- **{stack.Name}** — {cmds}");
        }
        return sb.ToString().TrimEnd();
    }

    private static IReadOnlyList<string> SafeGetFiles(string cwd, string pattern, SearchOption option)
    {
        try
        {
            return Directory.GetFiles(cwd, pattern, option);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return Array.Empty<string>();
        }
    }
}
