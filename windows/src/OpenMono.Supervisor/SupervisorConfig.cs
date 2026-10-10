using OpenMono.Windows.Hardware;
using OpenMono.Windows.Models;

namespace OpenMono.Windows.Supervisor;

/// <summary>
/// Supervisor owned settings. Inference stays native on 127.0.0.1:7474.
/// Caddy, SearXNG, and Scrapling run in Docker Desktop via the existing
/// docker/ compose definitions plus the windows/ override. The packaged
/// default is single box; a manual remote endpoint override enables
/// dual box client mode.
/// </summary>
public sealed record SupervisorConfig
{
    public const int DefaultLlamaPort = 7474;
    public const int DefaultAcpPort = 7475;
    public const int DefaultGatewayPort = 47480;

    public string Host { get; init; } = "127.0.0.1";
    public int LlamaPort { get; set; } = DefaultLlamaPort;
    public int AcpPort { get; set; } = DefaultAcpPort;
    public int GatewayPort { get; set; } = DefaultGatewayPort;
    public string ModelsDirectory { get; init; } = DefaultModelsDirectory();
    public string DataDirectory { get; init; } = DefaultDataDirectory();
    public string LogsDirectory { get; init; } = DefaultLogsDirectory();
    public string BinDirectory { get; init; } = DefaultBinDirectory();
    public string? RemoteEndpointOverride { get; init; }
    public string? ApiKey { get; init; }
    public bool VisionEnabled { get; init; } = true;
    public bool DockerServicesEnabled { get; init; } = true;

    /// <summary>
    /// When true, llama-server binds 0.0.0.0 so other machines on the LAN can
    /// use this box as inference. Default false: localhost only. An API key
    /// is required in LAN mode (see <see cref="ValidateLan"/>).
    /// </summary>
    public bool AllowLanConnections { get; set; } = false;

    /// <summary>Minimum API key length accepted for LAN serving.</summary>
    public const int MinLanApiKeyLength = 16;

    public string LlamaEndpoint => $"http://{Host}:{LlamaPort}";
    public string GatewayEndpoint => $"http://{Host}:{GatewayPort}";

    /// <summary>
    /// Address llama-server binds. LAN mode binds 0.0.0.0; otherwise the
    /// configured <see cref="Host"/> (localhost by default).
    /// </summary>
    public string BindHost => AllowLanConnections ? "0.0.0.0" : Host;

    /// <summary>
    /// Loopback URL used by health probes, the in-process agent, diagnostics,
    /// and settings. Always loopback, even in LAN mode: connecting to
    /// 0.0.0.0 does not route, and the agent runs on this machine.
    /// </summary>
    public string LoopbackEndpoint => $"http://127.0.0.1:{LlamaPort}";

    /// <summary>
    /// URLs to show LAN clients, one per local IPv4 address. Empty when no
    /// LAN address is assigned.
    /// </summary>
    public IReadOnlyList<string> LanAdvertisedUrls() =>
        LanNetwork.GetLanIPv4Addresses().Select(ip => $"http://{ip}:{LlamaPort}").ToList();

    /// <summary>
    /// Throws when LAN serving is on without a strong API key. Called before
    /// the server process starts so a keyless LAN bind can never happen.
    /// </summary>
    public void ValidateLan()
    {
        if (!AllowLanConnections)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new InvalidOperationException(
                "LAN serving is on but no API key is set. Set one on the Settings page before starting inference.");
        }

        if (ApiKey.Length < MinLanApiKeyLength)
        {
            throw new InvalidOperationException(
                $"LAN API key is too short ({ApiKey.Length} chars, minimum {MinLanApiKeyLength}). Generate a new one on the Settings page.");
        }
    }

    /// <summary>Generates a 64-char hex API key from 32 cryptographic random bytes.</summary>
    public static string GenerateApiKey()
    {
        Span<byte> bytes = stackalloc byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    public static string DefaultModelsDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenMono", "models");

    public static string DefaultDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openmono");

    public static string DefaultLogsDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenMono", "logs");

    public static string DefaultBinDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenMono", "bin");

    public LlamaCommandSpec BuildLlamaCommand(ModelTier tier, int threads, ModelTierSelector.ServerFlavor flavor)
    {
        string binary = Path.Combine(BinDirectory, "llama-server", FlavorDirectory(flavor), "llama-server.exe");
        int ctx = tier.EffectiveCtx(VisionEnabled);
        var (kvK, kvV) = tier.Tier switch
        {
            24 => ("q8_0", "q8_0"),
            _ => ("q4_0", "q4_0"),
        };
        // CPU tier keeps q8_0 KV like scripts/install.sh.
        if (tier.Tier == 0)
        {
            kvK = "q8_0";
            kvV = "q8_0";
        }

        var args = new List<string>
        {
            "--model", Path.Combine(ModelsDirectory, tier.ModelName),
            "--alias", tier.Alias,
            "--host", BindHost,
            "--port", LlamaPort.ToString(),
            "--ctx-size", ctx.ToString(),
            "--threads", threads.ToString(),
            "--n-gpu-layers", flavor == ModelTierSelector.ServerFlavor.Cpu ? "0" : "99",
            "--flash-attn", "on",
            "--cache-type-k", kvK,
            "--cache-type-v", kvV,
            "--batch-size", "2048",
            "--ubatch-size", flavor == ModelTierSelector.ServerFlavor.Cpu ? "1024" : "1024",
            "--parallel", "1",
            "--jinja",
            "--reasoning", "off",
            "--metrics",
        };
        if (VisionEnabled && !string.IsNullOrWhiteSpace(tier.Mmproj))
        {
            args.Add("--mmproj");
            args.Add(Path.Combine(ModelsDirectory, tier.Mmproj));
            args.Add("--image-min-tokens");
            args.Add("1024");
            args.Add("--image-max-tokens");
            args.Add("1280");
        }

        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            args.Add("--api-key");
            args.Add(ApiKey);
        }

        return new LlamaCommandSpec(binary, args, ctx, threads, tier.Alias, tier.Tier, flavor);
    }

    public static string FlavorDirectory(ModelTierSelector.ServerFlavor flavor) => flavor switch
    {
        ModelTierSelector.ServerFlavor.Cuda => "cuda",
        ModelTierSelector.ServerFlavor.Vulkan => "vulkan",
        _ => "cpu",
    };
}

public sealed record LlamaCommandSpec(
    string Binary,
    IReadOnlyList<string> Args,
    int CtxSize,
    int Threads,
    string Alias,
    int Tier,
    ModelTierSelector.ServerFlavor Flavor)
{
    public string ArgumentsLine => string.Join(' ', Args.Select(Quote));

    private static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}
