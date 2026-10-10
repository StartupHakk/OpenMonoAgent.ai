using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OpenMono.Windows.Supervisor;

namespace OpenMono.Windows.Tests;

/// <summary>
/// LAN inference serving: localhost by default, 0.0.0.0 with a required API
/// key when enabled. Health probes and the in-process agent stay loopback.
/// </summary>
public sealed class LanServeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oma-lan-test-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void Defaults_LoopbackOnly()
    {
        var config = new SupervisorConfig();
        Assert.False(config.AllowLanConnections);
        Assert.Equal("127.0.0.1", config.BindHost);
        Assert.Equal("http://127.0.0.1:7474", config.LlamaEndpoint);
        Assert.Equal("http://127.0.0.1:7474", config.LoopbackEndpoint);
        config.ValidateLan(); // must not throw when LAN is off, even keyless
    }

    [Fact]
    public void Lan_Binds_AllInterfaces_Probe_Stays_Loopback()
    {
        var config = new SupervisorConfig { AllowLanConnections = true, ApiKey = new string('k', 16) };
        Assert.Equal("0.0.0.0", config.BindHost);
        Assert.Equal("http://127.0.0.1:7474", config.LoopbackEndpoint);
        config.ValidateLan();
    }

    [Fact]
    public void Lan_Requires_ApiKey()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new SupervisorConfig { AllowLanConnections = true }.ValidateLan());
        Assert.Throws<InvalidOperationException>(() =>
            new SupervisorConfig { AllowLanConnections = true, ApiKey = "short" }.ValidateLan());
    }

    [Fact]
    public void Generated_Key_Passes_And_Is_Unique()
    {
        var first = SupervisorConfig.GenerateApiKey();
        var second = SupervisorConfig.GenerateApiKey();
        Assert.Equal(64, first.Length);
        Assert.NotEqual(first, second);
        Assert.True(first.All(c => Uri.IsHexDigit(c)));
        new SupervisorConfig { AllowLanConnections = true, ApiKey = first }.ValidateLan();
    }

    [Fact]
    public void Lan_Command_Uses_BindHost()
    {
        var tier = new Models.ModelTier(24, "label", "model.gguf", "http://x/model.gguf", 1, "full", "mmproj.gguf", "http://x/mmproj.gguf", 1, 196608, 172032, string.Empty, string.Empty);

        var local = new SupervisorConfig { LlamaPort = 7474 };
        var localSpec = local.BuildLlamaCommand(tier, 8, Hardware.ModelTierSelector.ServerFlavor.Cpu).Args.ToList();
        Assert.Contains("--host", localSpec);
        Assert.Equal("127.0.0.1", localSpec[localSpec.IndexOf("--host") + 1]);

        var lan = new SupervisorConfig { LlamaPort = 7474, AllowLanConnections = true, ApiKey = new string('k', 16) };
        var lanSpec = lan.BuildLlamaCommand(tier, 8, Hardware.ModelTierSelector.ServerFlavor.Cpu).Args.ToList();
        Assert.Equal("0.0.0.0", lanSpec[lanSpec.IndexOf("--host") + 1]);
        Assert.Contains("--api-key", lanSpec);
    }

    [Fact]
    public void Lan_IP_Discovery_Never_Returns_Loopback()
    {
        var addresses = LanNetwork.GetLanIPv4Addresses();
        foreach (var address in addresses)
        {
            Assert.True(IPAddress.TryParse(address, out var parsed));
            Assert.Equal(AddressFamily.InterNetwork, parsed.AddressFamily);
            Assert.False(IPAddress.IsLoopback(parsed));
        }

        Assert.Equal(addresses.Count, addresses.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Store_Round_Trips()
    {
        var path = Path.Combine(_dir, "app.json");
        var config = new SupervisorConfig
        {
            LlamaPort = 8081,
            AcpPort = 7479,
            GatewayPort = 47481,
            ModelsDirectory = Path.Combine(_dir, "models"),
            VisionEnabled = false,
            DockerServicesEnabled = false,
            AllowLanConnections = true,
            ApiKey = new string('k', 32),
            RemoteEndpointOverride = "http://192.168.1.10:7474",
        };
        SupervisorStore.Save(config, path);

        var loaded = SupervisorStore.Load(path);
        Assert.Equal(8081, loaded.LlamaPort);
        Assert.Equal(7479, loaded.AcpPort);
        Assert.Equal(47481, loaded.GatewayPort);
        Assert.Equal(config.ModelsDirectory, loaded.ModelsDirectory);
        Assert.False(loaded.VisionEnabled);
        Assert.False(loaded.DockerServicesEnabled);
        Assert.True(loaded.AllowLanConnections);
        Assert.Equal(config.ApiKey, loaded.ApiKey);
        Assert.Equal("http://192.168.1.10:7474", loaded.RemoteEndpointOverride);
        loaded.ValidateLan();
    }

    [Fact]
    public void Store_Missing_File_Returns_Defaults()
    {
        var loaded = SupervisorStore.Load(Path.Combine(_dir, "does-not-exist.json"));
        Assert.Equal(SupervisorConfig.DefaultLlamaPort, loaded.LlamaPort);
        Assert.False(loaded.AllowLanConnections);
        Assert.Null(loaded.ApiKey);
        Assert.Null(loaded.RemoteEndpointOverride);
    }

    [Fact]
    public void Store_Malformed_File_Returns_Defaults()
    {
        var path = Path.Combine(_dir, "app.json");
        File.WriteAllText(path, "{ not json");
        var loaded = SupervisorStore.Load(path);
        Assert.Equal(SupervisorConfig.DefaultLlamaPort, loaded.LlamaPort);
        Assert.False(loaded.AllowLanConnections);
    }

    [Fact]
    public void Store_Rejects_Out_Of_Range_Ports_And_Preserves_Unknown_Sections()
    {
        var path = Path.Combine(_dir, "app.json");
        File.WriteAllText(path,
            "{ \"supervisor\": { \"llama_port\": 99999, \"allow_lan_connections\": true },"
            + " \"firstRun\": { \"done\": true } }");

        var loaded = SupervisorStore.Load(path);
        Assert.Equal(SupervisorConfig.DefaultLlamaPort, loaded.LlamaPort);
        Assert.True(loaded.AllowLanConnections);

        SupervisorStore.Save(loaded, path);
        var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        Assert.True(root.TryGetProperty("firstRun", out var firstRun));
        Assert.True(firstRun.GetProperty("done").GetBoolean());
        Assert.True(root.GetProperty("supervisor").GetProperty("allow_lan_connections").GetBoolean());
    }
}
