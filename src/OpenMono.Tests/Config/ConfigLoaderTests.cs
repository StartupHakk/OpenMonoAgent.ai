using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;

namespace OpenMono.Tests.Config;

public class ConfigLoaderTests : IDisposable
{
    private readonly string _tempDir;

    // The loader reads these process-wide env vars (ApplyEnvironmentOverrides and the
    // AppConfig working-directory default). The agent runtime exports several of them
    // (OPENMONO_ENDPOINT/WORKSPACE/DATA_DIR/...), so scrub them for the duration of each
    // test to keep assertions deterministic, then restore the real values afterwards.
    private static readonly string[] LoaderEnvVars =
    [
        "OPENMONO_ENDPOINT", "OPENMONO_MODEL", "OPENMONO_API_KEY", "OPENMONO_WEB_GATEWAY",
        "OPENMONO_WEB_SEARCH", "OPENMONO_WEB_SCRAPE", "OPENMONO_WORKSPACE", "OPENMONO_HOST_WORKSPACE",
        "OPENMONO_DATA_DIR", "OPENMONO_CONTEXT_SIZE", "OPENMONO_MAX_OUTPUT_TOKENS", "OPENMONO_TOP_P",
        "OPENMONO_TOP_K", "OPENMONO_PRESENCE_PENALTY", "OPENMONO_MIN_P", "OPENMONO_REPETITION_PENALTY",
        "OPENMONO_MODEL_PRESET", "OPENMONO_PROVIDER", "OPENMONO_VISION_ENABLED",
    ];

    private readonly Dictionary<string, string?> _savedEnv = [];
    private readonly string _emptyDataDir = "";

    public ConfigLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        foreach (var name in LoaderEnvVars)
        {
            _savedEnv[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }

        // Point the loader's DataDirectory at an empty isolated dir so it never merges a
        // machine-global ~/.openmono/settings.json (which may carry ctx_size / malformed
        // values). Tests that want a user settings file set OPENMONO_DATA_DIR themselves.
        _emptyDataDir = Path.Combine(_tempDir, "isolated-data");
        Directory.CreateDirectory(_emptyDataDir);
        Environment.SetEnvironmentVariable("OPENMONO_DATA_DIR", _emptyDataDir);
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
    public void Load_ResolvesNonCanonicalWorkingDirectoryToAnAbsolutePath()
    {
        var sub = Path.Combine(_tempDir, "sub");
        Directory.CreateDirectory(sub);
        var nonCanonical = Path.Combine(_tempDir, "sub", "..", "sub");

        var config = ConfigLoader.Load(nonCanonical);

        config.WorkingDirectory.Should().Be(Path.GetFullPath(sub));
        config.WorkingDirectory.Should().NotContain("..");
    }

    [Fact]
    public void Load_ResolvesRelativeWorkingDirectoryAgainstCurrentDirectory()
    {
        var config = ConfigLoader.Load(".");

        Path.IsPathRooted(config.WorkingDirectory).Should().BeTrue();
        config.WorkingDirectory.Should().Be(Path.GetFullPath("."));
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

    [Theory]
    [InlineData("http://localhost:8080/v1", "http://localhost:8080")] // Strata install writes /v1
    [InlineData("http://localhost:8080/v1/", "http://localhost:8080")]
    [InlineData("http://localhost:8080", "http://localhost:8080")]   // llama: already root, unchanged
    [InlineData("http://localhost:7474", "http://localhost:7474")]
    [InlineData("https://api.openai.com", "https://api.openai.com")]
    public void Load_StripsTrailingV1_SoClientsDontDoubleIt(string input, string expected)
    {
        Environment.SetEnvironmentVariable("OPENMONO_ENDPOINT", input);
        try
        {
            var config = ConfigLoader.Load(_tempDir);
            // Callers append "/v1/...", so the stored base must not carry /v1.
            config.Llm.Endpoint.Should().Be(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENMONO_ENDPOINT", null);
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
        // Scope to this test's project file: a machine-global ~/.openmono/settings.json
        // is also read (and may itself warn), so don't assume it is the only warning.
        warnings.Should().ContainSingle(w => w.Contains("Malformed") && w.Contains(projectDir));
    }

    [Fact]
    public void Load_WebSearchScrapeAsBooleans_MergesWithoutWarning()
    {
        // 'openmono config set web.search true' writes a JSON boolean, but
        // WebConfig.Search/Scrape are strings. A bool must not throw and skip
        // the whole file (which previously dropped llm.endpoint as well).
        WriteProjectSettings("""
        {
            "llm": { "endpoint": "http://relay.example.ai:11910" },
            "web": { "gateway": "http://host.docker.internal:7480", "search": true, "scrape": false }
        }
        """);

        var warnings = new List<string>();
        var config = ConfigLoader.Load(_tempDir, warn: w => warnings.Add(w));

        warnings.Should().NotContain(w => w.Contains("Malformed"));
        config.Llm.Endpoint.Should().Be("http://relay.example.ai:11910");
        config.Web.Gateway.Should().Be("http://host.docker.internal:7480");
        config.Web.SearchEnabled.Should().BeTrue();
        config.Web.ScrapeEnabled.Should().BeFalse();
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
        foreach (var (name, value) in _savedEnv)
            Environment.SetEnvironmentVariable(name, value);

        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private void WriteProjectSettings(string json)
    {
        var projectDir = Path.Combine(_tempDir, ".openmono");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "settings.json"), json);
    }

    [Fact]
    public void Load_AppliesInferenceCtxSize_WithoutMarkingExplicit()
    {
        WriteProjectSettings("""{ "inference": { "ctx_size": 12345 } }""");

        var config = ConfigLoader.Load(_tempDir);

        config.Llm.ContextSize.Should().Be(12345);
        // Installer values stay non-explicit so live server detection
        // outranks them; only env / explicit llm.context_size stick.
        config.Llm.ContextSizeExplicit.Should().BeFalse();
    }

    [Fact]
    public void Load_ExplicitLlmContextSizeBeatsInference_AndIsExplicit()
    {
        WriteProjectSettings("""
        {
            "llm": { "context_size": 100000 },
            "inference": { "ctx_size": 12345 }
        }
        """);

        var config = ConfigLoader.Load(_tempDir);

        config.Llm.ContextSize.Should().Be(100000);
        config.Llm.ContextSizeExplicit.Should().BeTrue();
    }

    [Fact]
    public void Load_ContextSizeEnvBeatsInference()
    {
        WriteProjectSettings("""{ "inference": { "ctx_size": 12345 } }""");
        Environment.SetEnvironmentVariable("OPENMONO_CONTEXT_SIZE", "22222");
        try
        {
            var config = ConfigLoader.Load(_tempDir);

            config.Llm.ContextSize.Should().Be(22222);
            config.Llm.ContextSizeExplicit.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENMONO_CONTEXT_SIZE", null);
        }
    }

    [Fact]
    public void Load_VisionSettingsKeyEnablesImageInput()
    {
        WriteProjectSettings("""{ "inference": { "vision_enabled": true } }""");

        var config = ConfigLoader.Load(_tempDir);

        config.VisionEnabled.Should().BeTrue();
    }

    [Fact]
    public void Load_VisionEnvAppliesWhenSettingUnset()
    {
        Environment.SetEnvironmentVariable("OPENMONO_VISION_ENABLED", "1");
        try
        {
            var config = ConfigLoader.Load(_tempDir);

            config.VisionEnabled.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENMONO_VISION_ENABLED", null);
        }
    }

    [Fact]
    public void Load_VisionSettingFalseBeatsEnv()
    {
        WriteProjectSettings("""{ "inference": { "vision_enabled": false } }""");
        Environment.SetEnvironmentVariable("OPENMONO_VISION_ENABLED", "1");
        try
        {
            var config = ConfigLoader.Load(_tempDir);

            config.VisionEnabled.Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENMONO_VISION_ENABLED", null);
        }
    }

    [Fact]
    public void ParsePropsVision_TrueWhenServerReportsVision()
    {
        using var doc = JsonDocument.Parse("""{ "modalities": { "vision": true } }""");

        AppConfig.ParsePropsVision(doc.RootElement).Should().BeTrue();
    }

    [Fact]
    public void ParsePropsVision_FalseWhenServerReportsNoVision()
    {
        using var doc = JsonDocument.Parse("""{ "modalities": { "vision": false, "audio": true } }""");

        AppConfig.ParsePropsVision(doc.RootElement).Should().BeFalse();
    }

    [Fact]
    public void ParsePropsVision_NullWhenServerDoesNotAdvertiseIt()
    {
        using var doc = JsonDocument.Parse("""{ "model_alias": "gpt-ish" }""");

        AppConfig.ParsePropsVision(doc.RootElement).Should().BeNull();
    }
}
