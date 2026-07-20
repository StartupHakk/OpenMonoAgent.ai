using FluentAssertions;
using OpenMono.Utils;

namespace OpenMono.Tests.Utils;

public class StackDetectorTests : IDisposable
{
    private readonly string _dir;

    public StackDetectorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "omstack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private void Touch(string name, string content = "") =>
        File.WriteAllText(Path.Combine(_dir, name), content);

    private IReadOnlyList<StackCommand> CommandsFor(string stackName) =>
        StackDetector.Detect(_dir).Single(s => s.Name == stackName).Commands;

    private static string? CommandLabeled(IReadOnlyList<StackCommand> cmds, string label) =>
        cmds.FirstOrDefault(c => c.Label == label)?.Command;

    [Fact]
    public void Detect_EmptyDirectory_ReturnsNoStacks()
    {
        StackDetector.Detect(_dir).Should().BeEmpty();
    }

    [Fact]
    public void Detect_Node_Npm_ByDefault()
    {
        Touch("package.json", "{}");
        var cmds = CommandsFor("Node.js");
        CommandLabeled(cmds, "Install").Should().Be("npm install");
        CommandLabeled(cmds, "Test").Should().Be("npm test");
        CommandLabeled(cmds, "Build").Should().Be("npm run build");
    }

    [Fact]
    public void Detect_Node_PrefersPnpm_ThenYarn_FromLockfile()
    {
        Touch("package.json", "{}");
        Touch("pnpm-lock.yaml");
        CommandLabeled(CommandsFor("Node.js"), "Install").Should().Be("pnpm install");
    }

    [Fact]
    public void Detect_Python_Pip_WhenNoPoetryLock()
    {
        Touch("pyproject.toml");
        var cmds = CommandsFor("Python");
        CommandLabeled(cmds, "Install").Should().Be("pip install -e .");
        CommandLabeled(cmds, "Test").Should().Be("pytest");
    }

    [Fact]
    public void Detect_Python_Poetry_WhenPoetryLockPresent()
    {
        Touch("pyproject.toml");
        Touch("poetry.lock");
        CommandLabeled(CommandsFor("Python"), "Test").Should().Be("poetry run pytest");
    }

    [Fact]
    public void Detect_Go()
    {
        Touch("go.mod", "module example.com/x");
        var cmds = CommandsFor("Go");
        CommandLabeled(cmds, "Build").Should().Be("go build ./...");
        CommandLabeled(cmds, "Test").Should().Be("go test ./...");
    }

    [Fact]
    public void Detect_Rust()
    {
        Touch("Cargo.toml");
        CommandLabeled(CommandsFor("Rust"), "Test").Should().Be("cargo test");
    }

    [Fact]
    public void Detect_DotNet_Solution()
    {
        Touch("MyApp.sln");
        var cmds = CommandsFor(".NET");
        CommandLabeled(cmds, "Solution").Should().Be("dotnet build MyApp.sln");
        CommandLabeled(cmds, "Test").Should().Be("dotnet test");
    }

    [Fact]
    public void Detect_DotNet_CsprojOnly()
    {
        Touch("Lib.csproj");
        CommandLabeled(CommandsFor(".NET"), "Build").Should().Be("dotnet build");
    }

    [Fact]
    public void Detect_JavaMaven()
    {
        Touch("pom.xml");
        CommandLabeled(CommandsFor("Java (Maven)"), "Test").Should().Be("mvn test");
    }

    [Fact]
    public void Detect_Polyglot_ReturnsEveryStack()
    {
        Touch("package.json", "{}");
        Touch("go.mod");
        var names = StackDetector.Detect(_dir).Select(s => s.Name).ToList();
        names.Should().Contain(new[] { "Node.js", "Go" });
    }

    [Fact]
    public void BuildPromptSection_NoStacks_GivesGenericGuidance_NotDotnet()
    {
        var section = StackDetector.BuildPromptSection(StackDetector.Detect(_dir));
        section.Should().Contain("# Project Stack");
        section.Should().Contain("do not default to `dotnet`");
    }

    [Fact]
    public void BuildPromptSection_Node_ContainsNpmCommands()
    {
        Touch("package.json", "{}");
        var section = StackDetector.BuildPromptSection(StackDetector.Detect(_dir));
        section.Should().Contain("Node.js");
        section.Should().Contain("npm test");
        section.Should().NotContain("dotnet test");
    }
}
