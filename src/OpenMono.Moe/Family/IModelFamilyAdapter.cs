using OpenMono.Moe.Quant;

namespace OpenMono.Moe.Family;

public enum MosaicFamily : ushort
{
    MimoV26Flash = 1,
    Qwen38FlashNext = 2,
}

/// <summary>
/// Per-family ports over the shared RAM-arena / VRAM-cache / doorbell runtime.
/// A new MoE (GLM, DeepSeek, ...) is a new adapter. The runtime stays.
/// </summary>
public interface IModelFamilyAdapter
{
    string Id { get; }
    MosaicFamily Family { get; }
    string DisplayName { get; }
    ModelShape Shape { get; }

    /// <summary>
    /// False when another engine owns decode (Strata for Qwen3.8-Flash-Next).
    /// Mosaic must not pretend to run that family.
    /// </summary>
    bool OwnsRuntime { get; }

    string RuntimeOwner { get; }

    IReadOnlyList<ExpertRole> PackRoles { get; }

    int BitsPerWeightTenths(ExpertRole role, int layer);

    int ExpertsKeptAfterPrune { get; }

    int RecommendedContext { get; }
}
