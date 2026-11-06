namespace OpenMono.Win.Foundry;

// One physical GPU as seen by the host. Dedicated VRAM is what matters for
// the 24 GB floor; shared/system memory does not count.
public sealed record GpuInfo(string Name, long DedicatedVramBytes, bool IsNvidia)
{
    public double DedicatedVramGiB => DedicatedVramBytes / (1024.0 * 1024 * 1024);
}

// Abstraction over GPU discovery so unit tests run without hardware.
public interface IGpuProber
{
    Task<IReadOnlyList<GpuInfo>> ProbeAsync(CancellationToken ct = default);
}

// Pure parsers for the two discovery sources. Kept static and side-effect free
// so tests can cover every spelling without spawning processes.
public static class GpuCatalog
{
    // Parses `nvidia-smi --query-gpu=name,memory.total --format=csv,noheader,nounits`,
    // one `Name, MiB` row per line. Bad rows are skipped, never fatal.
    public static IReadOnlyList<GpuInfo> ParseNvidiaSmi(string output)
    {
        var gpus = new List<GpuInfo>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return gpus;
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split(',');
            if (parts.Length < 2)
            {
                continue;
            }

            var name = parts[0].Trim();
            if (!double.TryParse(
                    parts[1].Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var mib))
            {
                continue;
            }

            gpus.Add(new GpuInfo(name, (long)(mib * 1024 * 1024), IsNvidia: true));
        }

        return gpus;
    }

    // Parses `wmic path win32_VideoController get name,AdapterRAM /format:csv`
    // (Node,AdapterRAM,Name columns). AdapterRAM is a uint32 and wraps above
    // 4 GB, so callers must prefer nvidia-smi when it exists.
    public static IReadOnlyList<GpuInfo> ParseWmicCsv(string output)
    {
        var gpus = new List<GpuInfo>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return gpus;
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("Node,", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split(',');
            if (parts.Length < 3)
            {
                continue;
            }

            var name = parts[2].Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            long vram = 0;
            _ = long.TryParse(parts[1].Trim(), out vram);
            if (vram < 0)
            {
                vram = 0;
            }

            gpus.Add(new GpuInfo(
                name,
                vram,
                name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)));
        }

        return gpus;
    }
}

// Production prober: nvidia-smi first (exact VRAM), WMIC as a Windows fallback
// (names right, VRAM may wrap at 4 GB, hence the documented preference).
public sealed class SystemGpuProber : IGpuProber
{
    private readonly ICliRunner _runner;

    public SystemGpuProber(ICliRunner runner)
    {
        _runner = runner;
    }

    public async Task<IReadOnlyList<GpuInfo>> ProbeAsync(CancellationToken ct = default)
    {
        try
        {
            var smi = await _runner.RunAsync(
                "nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader,nounits", ct);
            if (smi.Succeeded)
            {
                var parsed = GpuCatalog.ParseNvidiaSmi(smi.StandardOutput);
                if (parsed.Count > 0)
                {
                    return parsed;
                }
            }
        }
        catch (Exception)
        {
            // nvidia-smi missing means no NVIDIA driver path; fall through.
        }

        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            var wmic = await _runner.RunAsync(
                "wmic", "path win32_VideoController get name,AdapterRAM /format:csv", ct);
            if (!wmic.Succeeded)
            {
                return [];
            }

            return GpuCatalog.ParseWmicCsv(wmic.StandardOutput);
        }
        catch (Exception)
        {
            return [];
        }
    }
}
