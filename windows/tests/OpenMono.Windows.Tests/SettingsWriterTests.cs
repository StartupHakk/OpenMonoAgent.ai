using System.Text.Json;
using OpenMono.Config;
using OpenMono.Windows.AgentHost;

namespace OpenMono.Windows.Tests;

/// <summary>
/// M1.1 acceptance: a settings file written by the wizard loads in
/// ConfigLoader with Llm.ContextSize (and endpoint/model) equal to what the
/// wizard chose, using snake_case keys OMA actually reads.
/// </summary>
public sealed class SettingsWriterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oma-settings-test-").FullName;
    private readonly Dictionary<string, string?> _savedEnv = new();

    public void Dispose()
    {
        foreach (var (key, value) in _savedEnv)
        {
            Environment.SetEnvironmentVariable(key, value);
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Writes_Snake_Case_Keys_ConfigLoader_Reads()
    {
        OmaSettingsWriter.Write(_dir, "http://127.0.0.1:7474", "qwen-test", 180224, visionEnabled: true, acpPort: 7475, acpEnabled: false);

        var raw = File.ReadAllText(SettingsPath);
        Assert.Contains("\"ctx_size\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"acp_server\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"vision_enabled\"", raw, StringComparison.Ordinal);
        Assert.Contains("\"context_size\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ctxSize\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"acpServer\"", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Round_Trips_Endpoint_Model_And_Context_Size_Through_ConfigLoader()
    {
        const string endpoint = "http://127.0.0.1:8081";
        const string model = "qwen-roundtrip";
        const int ctx = 180224;
        OmaSettingsWriter.Write(_dir, endpoint, model, ctx, visionEnabled: false, acpPort: 7476, acpEnabled: true);

        using var _ = IsolateEnv();
        var config = ConfigLoader.Load(workingDirectory: _dir, configPath: SettingsPath);

        Assert.Equal(endpoint, config.Llm.Endpoint);
        Assert.Equal(model, config.Llm.Model);
        Assert.Equal(ctx, config.Llm.ContextSize);
    }

    [Fact]
    public void Acp_And_Vision_Deserialize_With_Snake_Case()
    {
        // NOTE: ConfigLoader.MergeFromFile currently merges Llm/Agents/Web/
        // Inference/Permissions/Hooks/Providers/MCP/presets but not AcpServer
        // or VisionEnabled, so the file carries them while Load() drops them.
        // The in-process host sets those in memory from SupervisorConfig; this
        // test pins the file contract until upstream merges them.
        OmaSettingsWriter.Write(_dir, "http://127.0.0.1:7474", "qwen-test", 196608, visionEnabled: true, acpPort: 7479, acpEnabled: true);

        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(SettingsPath), JsonOptions.Default);
        Assert.NotNull(config);
        Assert.True(config!.VisionEnabled);
        Assert.NotNull(config.AcpServer);
        Assert.Equal(7479, config.AcpServer!.Port);
        Assert.True(config.AcpServer.Enabled);
        Assert.Equal(196608, config.Inference.CtxSize);
    }

    [Fact]
    public void Preserves_User_Tools_And_Llm_Tuning_Removes_Legacy_Twins()
    {
        File.WriteAllText(SettingsPath,
            "{\n" +
            "  \"llm\": { \"endpoint\": \"http://old\", \"model\": \"old\", \"temperature\": 0.3 },\n" +
            "  \"inference\": { \"ctxSize\": 123, \"ctx_size\": 123 },\n" +
            "  \"acpServer\": { \"enabled\": false, \"port\": 1 },\n" +
            "  \"permissions\": { \"tools\": { \"Custom\": { \"allow\": [\"x\"], \"deny\": [], \"ask\": [] } } }\n" +
            "}\n");

        OmaSettingsWriter.Write(_dir, "http://127.0.0.1:7474", "qwen-new", 196608, visionEnabled: false, acpPort: 7475, acpEnabled: false);

        var root = JsonDocument.Parse(File.ReadAllText(SettingsPath)).RootElement;
        Assert.Equal(0.3, root.GetProperty("llm").GetProperty("temperature").GetDouble());
        Assert.Equal("qwen-new", root.GetProperty("llm").GetProperty("model").GetString());
        Assert.True(root.GetProperty("permissions").GetProperty("tools").TryGetProperty("Custom", out _));
        var bash = root.GetProperty("permissions").GetProperty("tools").GetProperty("Bash");
        Assert.Contains("git *", ReadStrings(bash.GetProperty("allow")));
        Assert.False(root.GetProperty("inference").TryGetProperty("ctxSize", out _));
        Assert.False(root.TryGetProperty("acpServer", out _));
    }

    [Fact]
    public void Api_Key_Written_When_Provided_And_Preserved_Otherwise()
    {
        OmaSettingsWriter.Write(_dir, "http://127.0.0.1:7474", "m", 196608, visionEnabled: false, acpPort: 7475, acpEnabled: false, apiKey: "secret-1");
        Assert.Equal("secret-1", JsonDocument.Parse(File.ReadAllText(SettingsPath)).RootElement.GetProperty("llm").GetProperty("api_key").GetString());

        OmaSettingsWriter.Write(_dir, "http://127.0.0.1:7474", "m", 196608, visionEnabled: false, acpPort: 7475, acpEnabled: false);
        Assert.Equal("secret-1", JsonDocument.Parse(File.ReadAllText(SettingsPath)).RootElement.GetProperty("llm").GetProperty("api_key").GetString());
    }

    [Fact]
    public void Malformed_Existing_File_Is_Replaced()
    {
        File.WriteAllText(SettingsPath, "{ not json");
        OmaSettingsWriter.Write(_dir, "http://127.0.0.1:7474", "m", 196608, visionEnabled: false, acpPort: 7475, acpEnabled: false);

        using var _ = IsolateEnv();
        var config = ConfigLoader.Load(workingDirectory: _dir, configPath: SettingsPath);
        Assert.Equal("m", config.Llm.Model);
        Assert.Equal(196608, config.Llm.ContextSize);
    }

    [Fact]
    public void Permission_Merge_Keeps_User_Rules_And_Adds_Defaults()
    {
        File.WriteAllText(SettingsPath,
            "{ \"permissions\": { \"tools\": { \"Bash\": { \"allow\": [\"my-tool *\"], \"deny\": [], \"ask\": [] } } } }");

        OmaSettingsWriter.Write(_dir, "http://127.0.0.1:7474", "m", 196608, visionEnabled: false, acpPort: 7475, acpEnabled: false);

        var bash = JsonDocument.Parse(File.ReadAllText(SettingsPath)).RootElement
            .GetProperty("permissions").GetProperty("tools").GetProperty("Bash");
        var allow = ReadStrings(bash.GetProperty("allow"));
        Assert.Contains("my-tool *", allow);
        Assert.Contains("git *", allow);
        Assert.Equal(allow.Count, allow.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private static List<string> ReadStrings(JsonElement array) =>
        array.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToList();

    /// <summary>
    /// ConfigLoader reads the real user profile file and environment first;
    /// the explicit configPath merge wins for endpoint/model/context, but env
    /// overrides apply after, so pin the ones that affect assertions.
    /// </summary>
    private IsolatedEnv IsolateEnv() => new(_savedEnv, _dir);

    private sealed class IsolatedEnv : IDisposable
    {
        private readonly Dictionary<string, string?> _saved;

        public IsolatedEnv(Dictionary<string, string?> saved, string dir)
        {
            _saved = saved;
            foreach (var key in new[] { "OPENMONO_ENDPOINT", "OPENMONO_MODEL", "OPENMONO_CONTEXT_SIZE", "OPENMONO_DATA_DIR" })
            {
                if (!_saved.ContainsKey(key))
                {
                    _saved[key] = Environment.GetEnvironmentVariable(key);
                }
            }

            Environment.SetEnvironmentVariable("OPENMONO_ENDPOINT", null);
            Environment.SetEnvironmentVariable("OPENMONO_MODEL", null);
            Environment.SetEnvironmentVariable("OPENMONO_CONTEXT_SIZE", null);
            Environment.SetEnvironmentVariable("OPENMONO_DATA_DIR", Path.Combine(dir, "data"));
        }

        public void Dispose()
        {
            foreach (var (key, value) in _saved)
            {
                Environment.SetEnvironmentVariable(key, value);
            }

            _saved.Clear();
        }
    }
}
