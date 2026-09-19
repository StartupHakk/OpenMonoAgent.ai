using FluentAssertions;
using OpenMono.Config;

namespace OpenMono.Tests.Config;

public class DecisionSettingsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _envVars = [];

    public DecisionSettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void Load_WithDefaults_DecisionIsDisabled()
    {
        var config = ConfigLoader.Load(_tempDir);

        config.Decision.Enabled.Should().BeFalse();
        config.Decision.AutoThreshold.Should().Be(0.85);
        config.Decision.ReviewThreshold.Should().Be(0.6);
        config.Decision.MinConfidence.Should().Be(0.5);
        config.Decision.MaxSteps.Should().Be(64);
    }

    [Fact]
    public void Load_MergesDecisionSection()
    {
        var projectDir = Path.Combine(_tempDir, ".openmono");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "settings.json"), """
        {
            "decision": { "enabled": true, "auto_threshold": 0.9, "max_steps": 32 }
        }
        """);

        var config = ConfigLoader.Load(_tempDir);

        config.Decision.Enabled.Should().BeTrue();
        config.Decision.AutoThreshold.Should().Be(0.9);
        config.Decision.MaxSteps.Should().Be(32);
        config.Decision.ReviewThreshold.Should().Be(0.6);
    }

    [Fact]
    public void Load_ClampsOutOfRangeDecisionValues()
    {
        var projectDir = Path.Combine(_tempDir, ".openmono");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "settings.json"), """
        {
            "decision": { "auto_threshold": 9.5, "max_steps": 9999 }
        }
        """);

        var config = ConfigLoader.Load(_tempDir);

        config.Decision.AutoThreshold.Should().Be(1.0);
        config.Decision.MaxSteps.Should().Be(256);
    }

    [Fact]
    public void Load_EnvironmentOverridesDecision()
    {
        SetEnv("OPENMONO_DECISION_ENABLED", "1");
        SetEnv("OPENMONO_DECISION_AUTO_THRESHOLD", "0.7");
        SetEnv("OPENMONO_DECISION_MAX_STEPS", "16");

        var config = ConfigLoader.Load(_tempDir);

        config.Decision.Enabled.Should().BeTrue();
        config.Decision.AutoThreshold.Should().Be(0.7);
        config.Decision.MaxSteps.Should().Be(16);
    }

    [Fact]
    public void Load_EnvironmentBeatsSettingsFile()
    {
        var projectDir = Path.Combine(_tempDir, ".openmono");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "settings.json"), """
        {
            "decision": { "enabled": false, "auto_threshold": 0.9 }
        }
        """);
        SetEnv("OPENMONO_DECISION_ENABLED", "true");

        var config = ConfigLoader.Load(_tempDir);

        config.Decision.Enabled.Should().BeTrue();
        config.Decision.AutoThreshold.Should().Be(0.9);
    }

    private void SetEnv(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        _envVars.Add(name);
    }

    public void Dispose()
    {
        foreach (var name in _envVars)
            Environment.SetEnvironmentVariable(name, null);
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
        GC.SuppressFinalize(this);
    }
}
