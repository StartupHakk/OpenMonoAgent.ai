namespace OpenMono.Windows.Hardware;

/// <summary>
/// One combined hardware report for the first run wizard and diagnostics.
/// </summary>
public sealed record HardwareReport(
    IReadOnlyList<GpuInfo> Gpus,
    MemoryDetector.MemoryReport Memory,
    ModelTierSelector.TierSelection Selection,
    bool CudaAvailable)
{
    public static HardwareReport Collect()
    {
        var gpus = GpuDetector.Detect();
        var memory = MemoryDetector.Detect();
        bool cuda = gpus.Any(g => g.Vendor == GpuVendor.Nvidia) && CudaRuntimePresent();
        var selection = ModelTierSelector.Select(gpus, memory.TotalRamBytes, cuda);
        return new HardwareReport(gpus, memory, selection, cuda);
    }

    private static bool CudaRuntimePresent()
    {
        // Best effort: nvidia-smi present implies a usable NVIDIA driver stack.
        // The supervisor revalidates at server start and surfaces log output.
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name --format=csv,noheader",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!process.Start())
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
