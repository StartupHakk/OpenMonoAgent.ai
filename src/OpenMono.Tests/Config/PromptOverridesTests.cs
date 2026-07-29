using FluentAssertions;
using OpenMono.Config;

namespace OpenMono.Tests.Config;

public class PromptOverridesTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _dataDir;
    private readonly AppConfig _config;

    public PromptOverridesTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        _dataDir = Path.Combine(Path.GetTempPath(), $"openmono-test-data-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        Directory.CreateDirectory(_dataDir);
        _config = new AppConfig { WorkingDirectory = _workDir, DataDirectory = _dataDir };
    }

    [Fact]
    public void LoadSystemPrompt_NoOverrideFiles_ReturnsNull()
    {
        PromptOverrides.LoadSystemPrompt(_config).Should().BeNull();
    }

    [Fact]
    public void LoadSystemPrompt_GlobalOnly_ReturnsGlobalContent()
    {
        File.WriteAllText(Path.Combine(_dataDir, PromptOverrides.SystemPromptFile), "global prompt");

        PromptOverrides.LoadSystemPrompt(_config).Should().Be("global prompt");
    }

    [Fact]
    public void LoadSystemPrompt_ProjectAndGlobal_ProjectWins()
    {
        File.WriteAllText(Path.Combine(_dataDir, PromptOverrides.SystemPromptFile), "global prompt");
        var projectDir = Path.Combine(_workDir, ".openmono");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, PromptOverrides.SystemPromptFile), "project prompt");

        PromptOverrides.LoadSystemPrompt(_config).Should().Be("project prompt");
    }

    [Fact]
    public void LoadPlanPrompt_ProjectOnly_ReturnsProjectContent()
    {
        var projectDir = Path.Combine(_workDir, ".openmono");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, PromptOverrides.PlanPromptFile), "project plan prompt");

        PromptOverrides.LoadPlanPrompt(_config).Should().Be("project plan prompt");
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
        if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true);
    }
}
