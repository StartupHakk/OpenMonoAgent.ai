using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenMono.Windows.Models;

/// <summary>
/// One inference tier entry, mirrored from scripts/install.sh select_model.
/// Empty Sha256 means the checksum is not pinned yet and verification is
/// skipped with a recorded warning (see ChecksumVerifier).
/// </summary>
public sealed record ModelTier(
    [property: JsonPropertyName("tier")] int Tier,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("modelName")] string ModelName,
    [property: JsonPropertyName("modelUrl")] string ModelUrl,
    [property: JsonPropertyName("modelBytes")] long ModelBytes,
    [property: JsonPropertyName("accuracy")] string Accuracy,
    [property: JsonPropertyName("mmproj")] string Mmproj,
    [property: JsonPropertyName("mmprojUrl")] string MmprojUrl,
    [property: JsonPropertyName("mmprojBytes")] long MmprojBytes,
    [property: JsonPropertyName("ctxSize")] int CtxSize,
    [property: JsonPropertyName("ctxSizeVision")] int CtxSizeVision,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("mmprojSha256")] string MmprojSha256)
{
    public string Alias => ModelName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
        ? ModelName[..^".gguf".Length]
        : ModelName;

    public int EffectiveCtx(bool visionEnabled) => visionEnabled ? CtxSizeVision : CtxSize;
}

public sealed record LlamaServerAssets(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("cudaUrl")] string CudaUrl,
    [property: JsonPropertyName("vulkanUrl")] string VulkanUrl,
    [property: JsonPropertyName("cpuUrl")] string CpuUrl,
    [property: JsonPropertyName("cudaSha256")] string CudaSha256,
    [property: JsonPropertyName("vulkanSha256")] string VulkanSha256,
    [property: JsonPropertyName("cpuSha256")] string CpuSha256);

public sealed record RipgrepAsset(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>
/// Loads models.json and resolves tiers. Supports OPENMONO_MODEL_MIRROR style
/// host overrides the same way scripts/install.sh does.
/// </summary>
public sealed class ModelRegistry
{
    private readonly IReadOnlyList<ModelTier> _tiers;

    public LlamaServerAssets LlamaServer { get; }
    public RipgrepAsset Ripgrep { get; }

    public ModelRegistry(IReadOnlyList<ModelTier> tiers, LlamaServerAssets llamaServer, RipgrepAsset ripgrep)
    {
        _tiers = tiers;
        LlamaServer = llamaServer;
        Ripgrep = ripgrep;
    }

    public static ModelRegistry Load(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "models.json");
        if (!File.Exists(path))
        {
            var asmDir = Path.GetDirectoryName(typeof(ModelRegistry).Assembly.Location);
            var fallback = asmDir is not null ? Path.Combine(asmDir, "models.json") : null;
            if (fallback is not null && File.Exists(fallback))
            {
                path = fallback;
            }
        }

        using var stream = File.OpenRead(path);
        var doc = JsonSerializer.Deserialize<RegistryDocument>(stream, RegistryJson.Options)
            ?? throw new InvalidOperationException($"Cannot parse model registry at {path}");
        return new ModelRegistry(doc.Tiers, doc.LlamaServer, doc.Ripgrep);
    }

    public static ModelRegistry LoadFromJson(string json)
    {
        var doc = JsonSerializer.Deserialize<RegistryDocument>(json, RegistryJson.Options)
            ?? throw new InvalidOperationException("Cannot parse model registry JSON.");
        return new ModelRegistry(doc.Tiers, doc.LlamaServer, doc.Ripgrep);
    }

    public ModelTier ForTier(int tier) =>
        _tiers.FirstOrDefault(t => t.Tier == tier)
        ?? throw new InvalidOperationException($"No model tier {tier} in registry.");

    public IReadOnlyList<ModelTier> Tiers => _tiers;

    /// <summary>
    /// Applies a mirror host override, preserving path and filename.
    /// Mirrors OPENMONO_MODEL_MIRROR handling in scripts/install.sh.
    /// </summary>
    public static string ApplyMirror(string url, string? mirror)
    {
        if (string.IsNullOrWhiteSpace(mirror))
        {
            return url;
        }

        var original = new Uri(url);
        var host = mirror.TrimEnd('/');
        return host + original.PathAndQuery;
    }

    private sealed record RegistryDocument(
        [property: JsonPropertyName("tiers")] List<ModelTier> Tiers,
        [property: JsonPropertyName("llamaServer")] LlamaServerAssets LlamaServer,
        [property: JsonPropertyName("ripgrep")] RipgrepAsset Ripgrep);

    private static class RegistryJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
    }
}
