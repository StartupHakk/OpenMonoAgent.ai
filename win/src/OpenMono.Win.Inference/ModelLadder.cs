using System.Text.Json;

namespace OpenMono.Win.Inference;

// One row of the Windows tier table in scripts/models.json. The served alias
// is the file name without .gguf, as install.sh sets MODEL_ALIAS.
public sealed record ModelRung(
    string Key,
    string File,
    string Url,
    string? Mmproj,
    string? MmprojUrl,
    int Context,
    int VisionContext,
    string Kv,
    string Label,
    double? MinVramGiB)
{
    public string Alias => File.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? File[..^5] : File;

    public bool IsCpu => MinVramGiB is null;
}

// The Windows model ladder. It lives next to the Linux and macOS tables in
// scripts/models.json (platforms.windows.tiers of the default model) so one
// file names every GGUF the installers download. NVIDIA cards take the
// largest rung whose min_vram_gib fits their dedicated VRAM; machines
// without an NVIDIA GPU take the "cpu" rung.
public sealed class ModelLadder
{
    public const string Platform = "windows";

    public const string CpuKey = "cpu";

    private ModelLadder(IReadOnlyList<ModelRung> gpu, ModelRung cpu, string source)
    {
        Gpu = gpu;
        Cpu = cpu;
        Source = source;
    }

    // GPU rungs, largest first.
    public IReadOnlyList<ModelRung> Gpu { get; }

    public ModelRung Cpu { get; }

    public string Source { get; }

    public IEnumerable<ModelRung> All => Gpu.Append(Cpu);

    public static ModelLadder Load(string modelsJsonPath) =>
        Parse(File.ReadAllText(modelsJsonPath), modelsJsonPath);

    public static ModelLadder Parse(string json, string source = "models.json")
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var defaultId = root.TryGetProperty("default", out var d) ? d.GetString() : null;
        JsonElement? model = null;
        foreach (var m in root.GetProperty("models").EnumerateArray())
        {
            if (m.TryGetProperty("id", out var id) && id.GetString() == defaultId)
            {
                model = m;
                break;
            }
        }

        if (model is not { } entry
            || !entry.TryGetProperty("platforms", out var platforms)
            || !platforms.TryGetProperty(Platform, out var windows)
            || !windows.TryGetProperty("tiers", out var tiers))
        {
            throw new InvalidDataException($"{source} has no platforms.{Platform}.tiers for the default model '{defaultId}'.");
        }

        var gpu = new List<ModelRung>();
        ModelRung? cpu = null;
        foreach (var tier in tiers.EnumerateObject())
        {
            var t = tier.Value;
            var ctx = t.GetProperty("ctx").GetInt32();
            var rung = new ModelRung(
                tier.Name,
                t.GetProperty("name").GetString()!,
                t.GetProperty("url").GetString()!,
                Optional(t, "mmproj"),
                Optional(t, "mmproj_url"),
                ctx,
                t.TryGetProperty("ctx_vision", out var cv) ? cv.GetInt32() : ctx,
                Optional(t, "kv") ?? "q8_0",
                Optional(t, "label") ?? tier.Name,
                t.TryGetProperty("min_vram_gib", out var v) ? v.GetDouble() : null);

            if (tier.Name == CpuKey)
            {
                cpu = rung with { MinVramGiB = null };
            }
            else if (rung.MinVramGiB is not null)
            {
                gpu.Add(rung);
            }
        }

        if (cpu is null || gpu.Count == 0)
        {
            throw new InvalidDataException($"{source}: platforms.{Platform}.tiers needs a '{CpuKey}' tier and at least one GPU tier.");
        }

        return new ModelLadder(gpu.OrderByDescending(r => r.MinVramGiB).ToList(), cpu, source);
    }

    // vramBytes is the dedicated VRAM of the largest NVIDIA GPU, or null for
    // a machine CUDA cannot serve.
    public ModelRung Pick(long? vramBytes)
    {
        if (vramBytes is not { } bytes)
        {
            return Cpu;
        }

        var gib = bytes / (1024.0 * 1024 * 1024);
        return Gpu.FirstOrDefault(r => gib >= r.MinVramGiB) ?? Gpu[^1];
    }

    // --model / llm.model: a ladder alias or file name.
    public ModelRung? Find(string nameOrAlias) =>
        All.FirstOrDefault(r =>
            string.Equals(r.Alias, nameOrAlias, StringComparison.OrdinalIgnoreCase)
            || string.Equals(r.File, nameOrAlias, StringComparison.OrdinalIgnoreCase));

    public static string? Locate(Func<string, string?> getEnv, string? baseDirectory = null)
    {
        var repo = getEnv(LlamaCppConstants.RepoDirEnvVar);
        if (!string.IsNullOrWhiteSpace(repo))
        {
            var candidate = Path.Combine(repo, "scripts", "models.json");
            return File.Exists(candidate) ? candidate : null;
        }

        for (var dir = new DirectoryInfo(baseDirectory ?? AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "scripts", "models.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? Optional(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()
            : null;
}
