namespace OpenMono.Windows.Hardware;

/// <summary>
/// Inference tiers mirrored exactly from scripts/install.sh select_model.
/// 24 means 24GB+ VRAM, 16 means 16GB, 12 means 12GB, 0 means CPU fallback.
/// </summary>
public enum InferenceTier
{
    Cpu = 0,
    Tier12Gb = 12,
    Tier16Gb = 16,
    Tier24Gb = 24,
}

/// <summary>
/// Selects the inference tier and llama-server flavor from detected hardware.
/// Mirrors the VRAM thresholds and RAM warning in scripts/install.sh.
/// </summary>
public static class ModelTierSelector
{
    public const long Gb = 1024L * 1024L * 1024L;
    public const long Vram24Gb = 24L * Gb;
    public const long Vram16Gb = 16L * Gb;
    public const long Vram12Gb = 12L * Gb;
    public const long MinCpuRamBytes = 20L * Gb;

    public enum ServerFlavor
    {
        Cpu,
        Cuda,
        Vulkan,
    }

    public sealed record TierSelection(
        InferenceTier Tier,
        ServerFlavor Flavor,
        IReadOnlyList<string> Warnings);

    public static TierSelection Select(IReadOnlyList<GpuInfo> gpus, long totalRamBytes, bool cudaAvailable)
    {
        var warnings = new List<string>();
        var best = gpus
            .OrderByDescending(g => g.DedicatedBytes)
            .FirstOrDefault();

        if (best is null || best.DedicatedBytes <= 0)
        {
            return CpuSelection(totalRamBytes, warnings, "No GPU with readable VRAM was detected.");
        }

        long gib = best.DedicatedBytes / Gb;
        if (best.DedicatedBytes >= Vram24Gb)
        {
            return new TierSelection(
                InferenceTier.Tier24Gb,
                best.Vendor == GpuVendor.Nvidia && cudaAvailable ? ServerFlavor.Cuda : ServerFlavor.Vulkan,
                warnings);
        }

        if (best.DedicatedBytes >= Vram16Gb)
        {
            warnings.Add($"GPU VRAM is about {gib}GB, lower accuracy tier (16GB). For best results use 24GB or more VRAM.");
            return new TierSelection(
                InferenceTier.Tier16Gb,
                best.Vendor == GpuVendor.Nvidia && cudaAvailable ? ServerFlavor.Cuda : ServerFlavor.Vulkan,
                warnings);
        }

        if (best.DedicatedBytes >= Vram12Gb)
        {
            warnings.Add($"GPU VRAM is about {gib}GB, lower accuracy tier (12GB). For best results use 24GB or more VRAM.");
            return new TierSelection(
                InferenceTier.Tier12Gb,
                best.Vendor == GpuVendor.Nvidia && cudaAvailable ? ServerFlavor.Cuda : ServerFlavor.Vulkan,
                warnings);
        }

        warnings.Add($"Only about {gib}GB VRAM detected, the minimum is 12GB. Falling back to CPU mode.");
        return CpuSelection(totalRamBytes, warnings, null);
    }

    private static TierSelection CpuSelection(long totalRamBytes, List<string> warnings, string? reason)
    {
        if (!string.IsNullOrEmpty(reason))
        {
            warnings.Add(reason);
        }

        if (totalRamBytes > 0 && totalRamBytes < MinCpuRamBytes)
        {
            warnings.Add($"Only about {totalRamBytes / (double)Gb:0}GB RAM detected, the CPU model needs about 20GB. It may be slow or fail to load.");
        }

        return new TierSelection(InferenceTier.Cpu, ServerFlavor.Cpu, warnings);
    }

    public static InferenceTier TierFromVramMegabytes(long vramMb)
    {
        if (vramMb >= 24000)
        {
            return InferenceTier.Tier24Gb;
        }

        if (vramMb >= 16000)
        {
            return InferenceTier.Tier16Gb;
        }

        if (vramMb >= 12000)
        {
            return InferenceTier.Tier12Gb;
        }

        return InferenceTier.Cpu;
    }
}
