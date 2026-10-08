using System.Text.RegularExpressions;

namespace OpenMono.Windows.Hardware;

/// <summary>
/// Detects GPUs on Windows. All parsing helpers are pure and unit tested.
/// Runtime probing shells out to nvidia-smi and falls back to the
/// Win32_VideoController WMI table, so behavior degrades gracefully when
/// drivers or tooling are missing.
/// </summary>
public static class GpuDetector
{
    public static IReadOnlyList<GpuInfo> Detect()
    {
        var gpus = new List<GpuInfo>();
        gpus.AddRange(DetectNvidia());
        if (gpus.Count == 0)
        {
            gpus.AddRange(DetectViaWmi());
        }

        return gpus;
    }

    public static IReadOnlyList<GpuInfo> DetectNvidia()
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!process.Start())
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            if (process.ExitCode != 0)
            {
                return [];
            }

            return ParseNvidiaSmi(output);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Parses nvidia-smi CSV output (name, memory.total MiB, driver version).
    /// Same query shape as scripts/install.sh and the openmono launcher.
    /// </summary>
    public static IReadOnlyList<GpuInfo> ParseNvidiaSmi(string csv)
    {
        var result = new List<GpuInfo>();
        foreach (var rawLine in csv.Split('\n'))
        {
            var line = rawLine.Trim();
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
            if (!long.TryParse(parts[1].Trim(), out var mib))
            {
                continue;
            }

            var driver = parts.Length >= 3 ? parts[2].Trim() : null;
            result.Add(new GpuInfo(
                name,
                GpuVendor.Nvidia,
                mib * 1024L * 1024L,
                0,
                string.IsNullOrWhiteSpace(driver) ? null : driver));
        }

        return result;
    }

    /// <summary>
    /// Best effort WMI fallback for AMD, Intel, and driverless NVIDIA cards.
    /// Runs wmic when available, otherwise returns empty.
    /// </summary>
    public static IReadOnlyList<GpuInfo> DetectViaWmi()
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "wmic",
                Arguments = "path win32_VideoController get Name,AdapterRAM,DriverVersion /format:csv",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!process.Start())
            {
                return [];
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15_000);
            if (process.ExitCode != 0)
            {
                return [];
            }

            return ParseWmicVideoController(output);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Parses wmic CSV output for Win32_VideoController rows.
    /// </summary>
    public static IReadOnlyList<GpuInfo> ParseWmicVideoController(string csv)
    {
        var result = new List<GpuInfo>();
        string[]? headers = null;
        foreach (var rawLine in csv.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var cells = SplitCsvLine(line);
            if (headers is null)
            {
                headers = cells;
                continue;
            }

            var row = headers.Zip(cells, (h, c) => (h.Trim(), c.Trim()))
                .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase);
            var name = row.TryGetValue("Name", out var n) ? n : "Unknown GPU";
            var driver = row.TryGetValue("DriverVersion", out var d) ? d : null;
            long adapterBytes = 0;
            if (row.TryGetValue("AdapterRAM", out var ramText))
            {
                // AdapterRAM is a uint32 and wraps above 4GB, so treat huge
                // values as unknown rather than as fact.
                if (uint.TryParse(ramText, out var adapterRam) && adapterRam < uint.MaxValue)
                {
                    adapterBytes = adapterRam;
                }
            }

            result.Add(new GpuInfo(
                string.IsNullOrWhiteSpace(name) ? "Unknown GPU" : name,
                ClassifyVendor(name),
                adapterBytes,
                0,
                string.IsNullOrWhiteSpace(driver) ? null : driver));
        }

        return result;
    }

    public static GpuVendor ClassifyVendor(string name)
    {
        if (Regex.IsMatch(name, @"nvidia|geforce|quadro|tesla", RegexOptions.IgnoreCase))
        {
            return GpuVendor.Nvidia;
        }

        if (Regex.IsMatch(name, @"\bamd\b|radeon|rx |vega|rdna|instinct", RegexOptions.IgnoreCase))
        {
            return GpuVendor.Amd;
        }

        if (Regex.IsMatch(name, @"\bintel\b|arc|iris|uhd|xeon phi", RegexOptions.IgnoreCase))
        {
            return GpuVendor.Intel;
        }

        return GpuVendor.Unknown;
    }

    private static string[] SplitCsvLine(string line)
    {
        // wmic CSV output never quotes fields, plain split is sufficient.
        return line.Split(',');
    }
}
