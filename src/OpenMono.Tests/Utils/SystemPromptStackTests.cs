using FluentAssertions;
using OpenMono.Config;
using OpenMono.Utils;

namespace OpenMono.Tests.Utils;

public class SystemPromptStackTests : IDisposable
{
    private readonly string _dir;

    public SystemPromptStackTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "omprompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private Task<string> BuildForAsync() =>
        SystemPrompt.BuildAsync(new AppConfig { WorkingDirectory = _dir });

    [Fact]
    public void Base_IdentityIsStackNeutral_NotDotNetOnly()
    {
        SystemPrompt.Base.Should().NotContain(".NET full-stack coding agent");
        SystemPrompt.Base.Should().NotContain("# .NET Development");
        SystemPrompt.Base.Should().Contain("# Development & Verification");
    }

    [Fact]
    public async Task BuildAsync_NodeProject_InjectsNpmCommands()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "package.json"), "{}");
        var prompt = await BuildForAsync();
        prompt.Should().Contain("# Project Stack (auto-detected)");
        prompt.Should().Contain("npm test");
    }

    [Fact]
    public async Task BuildAsync_PythonProject_InjectsPytest()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "pyproject.toml"), "");
        var prompt = await BuildForAsync();
        prompt.Should().Contain("pytest");
    }

    [Fact]
    public async Task BuildAsync_DotNetProject_StillUsesDotnet()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "App.csproj"), "<Project/>");
        var prompt = await BuildForAsync();
        prompt.Should().Contain("dotnet build");
        prompt.Should().Contain("dotnet test");
    }

    [Fact]
    public async Task BuildAsync_UnknownStack_DoesNotDefaultToDotnet()
    {
        var prompt = await BuildForAsync();
        prompt.Should().Contain("do not default to `dotnet`");
    }
}
