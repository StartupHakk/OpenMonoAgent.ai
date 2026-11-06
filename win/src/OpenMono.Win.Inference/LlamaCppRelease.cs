using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenMono.Win.Inference;

// llama.cpp build flavors OpenMono runs on Windows, in fallback order: CUDA on
// NVIDIA (the Linux server-cuda image), Vulkan on any GPU the CUDA build
// cannot use, then CPU (the Linux server image).
public enum Accel
{
    Cuda,
    Vulkan,
    Cpu,
}

public sealed record ReleaseAsset(string Name, string Url, long? Size);

public sealed record LlamaRelease(string Tag, IReadOnlyList<ReleaseAsset> Assets);

// One downloadable Windows build: the llama-server zip plus, for CUDA, the
// matching cudart zip with the CUDA runtime DLLs (no CUDA toolkit needed).
public sealed record LlamaBuildChoice(
    string Tag,
    Accel Accel,
    Version? CudaVersion,
    ReleaseAsset Server,
    ReleaseAsset? Cudart)
{
    public string Flavor => LlamaCppRelease.Flavor(Accel, CudaVersion);

    public string Id => $"{Tag}-{Flavor}";
}

public static partial class LlamaCppRelease
{
    public static string Flavor(Accel accel, Version? cuda) => accel switch
    {
        Accel.Cuda => $"cuda-{cuda?.Major ?? 0}.{cuda?.Minor ?? 0}",
        Accel.Vulkan => "vulkan",
        _ => "cpu",
    };

    public static string Describe(Accel accel) => accel switch
    {
        Accel.Cuda => "CUDA",
        Accel.Vulkan => "Vulkan",
        _ => "CPU",
    };

    // GitHub releases API, newest first. Bad JSON is an empty list.
    public static IReadOnlyList<LlamaRelease> ParseReleases(string json)
    {
        var releases = new List<LlamaRelease>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : [doc.RootElement];
            foreach (var item in items)
            {
                if (!item.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (item.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                {
                    continue;
                }

                var assets = new List<ReleaseAsset>();
                if (item.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in list.EnumerateArray())
                    {
                        if (asset.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                            && asset.TryGetProperty("browser_download_url", out var url) && url.ValueKind == JsonValueKind.String)
                        {
                            long? size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : null;
                            assets.Add(new ReleaseAsset(name.GetString()!, url.GetString()!, size));
                        }
                    }
                }

                releases.Add(new LlamaRelease(tag.GetString()!, assets));
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return releases;
    }

    // The newest b<N> build (or the pinned tag) that ships the flavor. For
    // CUDA the highest build the driver supports wins; a driver older than
    // every CUDA build yields null so the caller falls back to Vulkan.
    public static LlamaBuildChoice? Pick(
        IReadOnlyList<LlamaRelease> releases,
        Accel accel,
        Version? driverCuda,
        string? pinnedTag = null)
    {
        var candidates = releases
            .Where(r => pinnedTag is null
                ? BuildNumber(r.Tag) is not null
                : string.Equals(r.Tag, pinnedTag, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => BuildNumber(r.Tag) ?? 0);

        foreach (var release in candidates)
        {
            var choice = PickFrom(release, accel, driverCuda);
            if (choice is not null)
            {
                return choice;
            }
        }

        return null;
    }

    private static LlamaBuildChoice? PickFrom(LlamaRelease release, Accel accel, Version? driverCuda)
    {
        var tag = release.Tag;
        ReleaseAsset? Named(string name) =>
            release.Assets.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

        switch (accel)
        {
            case Accel.Cpu:
                return Named($"llama-{tag}-bin-win-cpu-x64.zip") is { } cpu
                    ? new LlamaBuildChoice(tag, Accel.Cpu, null, cpu, null)
                    : null;

            case Accel.Vulkan:
                return Named($"llama-{tag}-bin-win-vulkan-x64.zip") is { } vulkan
                    ? new LlamaBuildChoice(tag, Accel.Vulkan, null, vulkan, null)
                    : null;

            default:
                if (driverCuda is null)
                {
                    return null;
                }

                var prefix = $"llama-{tag}-bin-win-cuda-";
                var builds = release.Assets
                    .Select(a => (Asset: a, Version: CudaAssetVersion(a.Name, prefix)))
                    .Where(x => x.Version is not null && x.Version <= driverCuda)
                    .OrderByDescending(x => x.Version);
                foreach (var (asset, version) in builds)
                {
                    var cudart = Named($"cudart-llama-bin-win-cuda-{version!.Major}.{version.Minor}-x64.zip");
                    if (cudart is not null)
                    {
                        return new LlamaBuildChoice(tag, Accel.Cuda, version, asset, cudart);
                    }
                }

                return null;
        }
    }

    public static int? BuildNumber(string tag) =>
        BuildTag().Match(tag) is { Success: true } m
            && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

    // `nvidia-smi` prints "CUDA Version: 12.8" in its banner: the newest CUDA
    // runtime the installed driver can run.
    public static Version? ParseDriverCudaVersion(string nvidiaSmiOutput) =>
        DriverCuda().Match(nvidiaSmiOutput ?? "") is { Success: true } m
            ? new Version(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
            : null;

    private static Version? CudaAssetVersion(string name, string prefix)
    {
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith("-x64.zip", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var middle = name[prefix.Length..^"-x64.zip".Length];
        return Version.TryParse(middle, out var v) && v.Build < 0 ? v : null;
    }

    [GeneratedRegex(@"^b(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex BuildTag();

    [GeneratedRegex(@"CUDA Version:\s*(\d+)\.(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DriverCuda();
}
