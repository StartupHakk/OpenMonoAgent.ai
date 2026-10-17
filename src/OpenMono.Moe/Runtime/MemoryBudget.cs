using OpenMono.Moe.Family;

namespace OpenMono.Moe.Runtime;

public sealed record MemorySlice(
    string Name,
    double VramGiB,
    double RamGiB,
    bool Estimated)
{
    public double CombinedGiB => VramGiB + RamGiB;
}

public sealed record MemoryBudgetReport
{
    public required string ConfigId { get; init; }
    public required IReadOnlyList<MemorySlice> Slices { get; init; }
    public required double VramLimitGiB { get; init; }
    public required double RamLimitGiB { get; init; }
    public required double CombinedLimitGiB { get; init; }
    public double VramUsedGiB => Slices.Sum(s => s.VramGiB);
    public double RamUsedGiB => Slices.Sum(s => s.RamGiB);
    public double CombinedUsedGiB => VramUsedGiB + RamUsedGiB;
    public bool Fits =>
        VramUsedGiB <= VramLimitGiB &&
        RamUsedGiB <= RamLimitGiB &&
        CombinedUsedGiB <= CombinedLimitGiB;
}

/// <summary>
/// Consumer-box envelope from docs/MIMO.md: 20 GiB VRAM / 57 GiB RAM / 77 GiB
/// combined left for weights+KV after OS, CUDA, and scratch.
/// </summary>
public static class MemoryBudget
{
    public const double ConsumerVramGiB = 20.0;
    public const double ConsumerRamGiB = 57.0;
    public const double ConsumerCombinedGiB = 77.0;

    public static double GaKvBytesPerTokenFp16(ModelShape shape) =>
        shape.GaLayers * shape.GaKvHeads * (shape.HeadDim + shape.VHeadDim) * 2.0;

    public static double SwaKvBytesFp16(ModelShape shape) =>
        shape.SwaLayers * shape.SwaKvHeads * (shape.HeadDim + shape.VHeadDim) * 2.0 * shape.SlidingWindow;

    public static double KvGiB(ModelShape shape, int context, double kvBytesPerElementRelativeToFp16)
    {
        double ga = GaKvBytesPerTokenFp16(shape) * context * kvBytesPerElementRelativeToFp16;
        double swa = SwaKvBytesFp16(shape) * kvBytesPerElementRelativeToFp16;
        return (ga + swa) / (1024.0 * 1024.0 * 1024.0);
    }

    public static MemoryBudgetReport Evaluate(MosaicRecommendedConfig config)
    {
        var slices = new List<MemorySlice>
        {
            new("weights.vram", config.WeightVramGiB, 0, config.WeightsEstimated),
            new("weights.ram", 0, config.WeightRamGiB, config.WeightsEstimated),
            new("kv", config.KvVramGiB, 0, Estimated: false),
            new("mtp", config.MtpVramGiB, config.MtpRamGiB, config.MtpEstimated),
        };
        return new MemoryBudgetReport
        {
            ConfigId = config.Id,
            Slices = slices,
            VramLimitGiB = ConsumerVramGiB,
            RamLimitGiB = ConsumerRamGiB,
            CombinedLimitGiB = ConsumerCombinedGiB,
        };
    }
}

/// <summary>
/// Quality-max pack that still fits 24+64 with no SSD in the hot path.
/// File sizes that are Hugging Face-measured are marked Estimated=false.
/// </summary>
public sealed record MosaicRecommendedConfig
{
    public required string Id { get; init; }
    public required string FamilyId { get; init; }
    public required int ExpertsKept { get; init; }
    public required int Context { get; init; }
    public required double WeightVramGiB { get; init; }
    public required double WeightRamGiB { get; init; }
    public required bool WeightsEstimated { get; init; }
    public required double KvVramGiB { get; init; }
    public required double MtpVramGiB { get; init; }
    public required double MtpRamGiB { get; init; }
    public required bool MtpEstimated { get; init; }

    public static MosaicRecommendedConfig MimoFlashRl
    {
        get
        {
            var shape = new MimoV26FlashAdapter().Shape;
            // REAP50 Q2_K = 61.64 GiB measured. Split ~14 GiB trunk+hot on GPU,
            // remainder pinned in RAM. MTP Q8 = 2.22 GiB measured, staged in RAM
            // and a small working copy in VRAM.
            double kv = MemoryBudget.KvGiB(shape, context: 131_072, kvBytesPerElementRelativeToFp16: 0.5);
            return new MosaicRecommendedConfig
            {
                Id = "mimo-flash-rl-reap50-mixed-a",
                FamilyId = MimoV26FlashAdapter.FamilyId,
                ExpertsKept = 128,
                Context = 131_072,
                WeightVramGiB = 14.0,
                WeightRamGiB = 47.64,
                WeightsEstimated = false,
                KvVramGiB = kv,
                MtpVramGiB = 0.4,
                MtpRamGiB = 2.22,
                MtpEstimated = false,
            };
        }
    }
}
