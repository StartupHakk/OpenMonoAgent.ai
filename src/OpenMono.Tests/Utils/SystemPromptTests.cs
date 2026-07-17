using FluentAssertions;
using OpenMono.Config;
using OpenMono.Utils;

namespace OpenMono.Tests.Utils;

public class SystemPromptTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _dataDir;

    public SystemPromptTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        _dataDir = Path.Combine(Path.GetTempPath(), $"openmono-test-data-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        Directory.CreateDirectory(_dataDir);
    }

    [Fact]
    public async Task BuildAsync_NoOverride_UsesHardcodedBase()
    {
        var config = new AppConfig { WorkingDirectory = _workDir, DataDirectory = _dataDir };

        var prompt = await SystemPrompt.BuildAsync(config);

        prompt.Should().StartWith(SystemPrompt.Base);
    }

    [Fact]
    public async Task BuildAsync_WithProjectOverride_ReplacesBaseVerbatim()
    {
        var projectDir = Path.Combine(_workDir, ".openmono");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, PromptOverrides.SystemPromptFile), "CUSTOM SYSTEM PROMPT");

        var config = new AppConfig { WorkingDirectory = _workDir, DataDirectory = _dataDir };

        var prompt = await SystemPrompt.BuildAsync(config);

        prompt.Should().StartWith("CUSTOM SYSTEM PROMPT");
        prompt.Should().NotContain(SystemPrompt.Base);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir)) Directory.Delete(_workDir, recursive: true);
        if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true);
    }
}
