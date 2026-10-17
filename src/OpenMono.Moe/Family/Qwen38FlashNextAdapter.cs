using OpenMono.Moe.Quant;

namespace OpenMono.Moe.Family;

/// <summary>
/// Registry entry only. Decode stays on upstream Strata (scripts/strata.sh).
/// Mosaic must not load or pack this family.
/// </summary>
public sealed class Qwen38FlashNextAdapter : IModelFamilyAdapter
{
    public const string FamilyId = "qwen3.8-flash-next";

    public string Id => FamilyId;
    public MosaicFamily Family => MosaicFamily.Qwen38FlashNext;
    public string DisplayName => "Qwen3.8-Flash-Next (Strata)";
    public bool OwnsRuntime => false;
    public string RuntimeOwner => "strata";
    public IReadOnlyList<ExpertRole> PackRoles { get; } = [];
    public int ExpertsKeptAfterPrune => 256;
    public int RecommendedContext => 131_072;

    public ModelShape Shape { get; } = new()
    {
        Architecture = "Qwen3.8-Flash-Next",
        TotalParameters = 125_000_000_000,
        ActiveParameters = 6_000_000_000,
        HiddenSize = 0,
        VocabSize = 0,
        NumLayers = 0,
        NRoutedExperts = 512,
        NumExpertsPerTok = 0,
        MoeIntermediateSize = 0,
        DenseIntermediateSize = 0,
        NumAttentionHeads = 0,
        GaKvHeads = 0,
        SwaKvHeads = 0,
        HeadDim = 0,
        VHeadDim = 0,
        SlidingWindow = 0,
        MaxContext = 0,
        HybridLayerPattern = [],
        MoeLayerFreq = [],
        ScoringFunc = "strata",
        TopkMethod = "strata",
        NormTopkProb = false,
        SharedExperts = 0,
        MtpDraftTokens = 0,
        MtpLayers = 0,
    };

    public int BitsPerWeightTenths(ExpertRole role, int layer) =>
        throw new NotSupportedException(
            "Qwen3.8-Flash-Next is served by Strata (scripts/strata.sh). Mosaic does not pack it.");
}
