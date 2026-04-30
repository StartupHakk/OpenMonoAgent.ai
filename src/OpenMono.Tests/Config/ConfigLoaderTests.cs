using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;

namespace OpenMono.Tests.Config;

public class ConfigLoaderTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void Load_WithDefaults_ReturnsDefaultConfig()
    {
        var config = ConfigLoader.Load(_tempDir);

        config.Llm.Endpoint.Should().Be("http://localhost:7474");
        config.Llm.Model.Should().Be("");
        config.Llm.ContextSize.Should().Be(196608);
    }

    [Fact]
    public void Load_MergesUserConfig()
    {
        var dataDir = Path.Combine(_tempDir, ".openmono");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "settings.json"), """
        {
            "llm": { "model": "custom-model", "temperature": 0.5 }
        }
        """);

        Environment.SetEnvironmentVariable("OPENMONO_DATA_DIR", dataDir);
        try
        {
            var config = ConfigLoader.Load(_tempDir);

            config.Llm.Model.Should().Be("custom-model");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENMONO_DATA_DIR", null);
        }
    }

    [Fact]
    public void Load_MergesProviders()
    {
        var projectDir = Path.Combine(_tempDir, ".openmono");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "settings.json"), """
        {
            "providers": {
                "openai": { "api_key": "sk-test", "model": "gpt-4o", "active": true }
            }
        }
        """);

        var config = ConfigLoader.Load(_tempDir);
        config.Providers.Should().ContainKey("openai");
        config.Providers["openai"].ApiKey.Should().Be("sk-test");
        config.Providers["openai"].Active.Should().BeTrue();
    }

    [Fact]
    public void Load_MergesMcpServers()
    {
        var projectDir = Path.Combine(_tempDir, ".openmono");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "settings.json"), """
        {
            "mcp_servers": {
                "my-tools": { "command": "npx", "args": ["-y", "@my/mcp"], "enabled": true }
            }
        }
        """);

        var config = ConfigLoader.Load(_tempDir);
        config.McpServers.Should().ContainKey("my-tools");
        config.McpServers["my-tools"].Command.Should().Be("npx");
        config.McpServers["my-tools"].Enabled.Should().BeTrue();
    }

    [Fact]
    public void Load_EnvironmentOverrides()
    {
        Environment.SetEnvironmentVariable("OPENMONO_ENDPOINT", "http://custom:9090");
        Environment.SetEnvironmentVariable("OPENMONO_MODEL", "test-model");
        try
        {
            var config = ConfigLoader.Load(_tempDir);
            config.Llm.Endpoint.Should().Be("http://custom:9090");
            config.Llm.Model.Should().Be("test-model");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENMONO_ENDPOINT", null);
            Environment.SetEnvironmentVariable("OPENMONO_MODEL", null);
        }
    }

    [Fact]
    public void Load_MalformedConfig_WarnsAndContinues()
    {
        var projectDir = Path.Combine(_tempDir, ".openmono");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "settings.json"), "not valid json {{{");

        var warnings = new List<string>();
        var config = ConfigLoader.Load(_tempDir, warn: w => warnings.Add(w));

        config.Should().NotBeNull();
        warnings.Should().ContainSingle(w => w.Contains("Malformed"));
    }

    [Fact]
    public void Load_MergesPermissions_Additively()
    {
        var projectDir = Path.Combine(_tempDir, ".openmono");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "settings.json"), """
        {
            "permissions": {
                "tools": {
                    "Bash": { "allow": ["git *"], "deny": ["rm -rf /"], "ask": ["*"] }
                }
            }
        }
        """);

        var config = ConfigLoader.Load(_tempDir);
        config.Permissions.Tools.Should().ContainKey("Bash");
        config.Permissions.Tools["Bash"].Allow.Should().Contain("git *");
        config.Permissions.Tools["Bash"].Deny.Should().Contain("rm -rf /");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
