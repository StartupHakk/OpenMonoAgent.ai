namespace OpenMono.Windows.Hardware;

/// <summary>
/// System memory and CPU probing with pure parse helpers for tests.
/// </summary>
public static class MemoryDetector
{
    public sealed record MemoryReport(
        long TotalRamBytes,
        int LogicalCores,
        int PhysicalCores,
        string? PowerProfileNote);

    public static MemoryReport Detect()
    {
        long totalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (totalBytes <= 0)
        {
            totalBytes = Environment.WorkingSet;
        }

        int logical = Environment.ProcessorCount;
        int physical = DetectPhysicalCores() is { } cores && cores > 0 ? cores : logical;

        return new MemoryReport(totalBytes, logical, physical, null);
    }

    /// <summary>
    /// Best effort physical core count via wmic. Null when unavailable.
    /// Thread count for llama-server should prefer physical cores because
    /// SMT hurts llama.cpp throughput (same note as scripts/install.sh).
    /// </summary>
    public static int? DetectPhysicalCores()
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "wmic",
                Arguments = "cpu get NumberOfCores /format:csv",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!process.Start())
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(15_000);
            if (process.ExitCode != 0)
            {
                return null;
            }

            return ParseWmicCpuCores(output);
        }
        catch
        {
            return null;
        }
    }

    public static int? ParseWmicCpuCores(string csv)
    {
        int total = 0;
        string[]? headers = null;
        foreach (var rawLine in csv.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var cells = line.Split(',');
            if (headers is null)
            {
                headers = cells;
                continue;
            }

            var index = Array.FindIndex(headers, h => h.Trim().Equals("NumberOfCores", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index >= cells.Length)
            {
                continue;
            }

            if (int.TryParse(cells[index].Trim(), out var cores))
            {
                total += cores;
            }
        }

        return total > 0 ? total : null;
    }

    /// <summary>
    /// Recommends the llama-server --threads value: physical cores.
    /// </summary>
    public static int RecommendThreads(int physicalCores, int logicalCores)
    {
        if (physicalCores > 0)
        {
            return physicalCores;
        }

        return Math.Max(1, logicalCores);
    }
}
