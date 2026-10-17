using OpenMono.Moe.Quant;

namespace OpenMono.Moe.Family;

/// <summary>
/// Xiaomi MiMo-V2.6-Flash-RL. Numbers match the official config.json
/// (Hugging Face XiaomiMiMo/MiMo-V2.6-Flash-RL). First Mosaic-owned family.
/// </summary>
public sealed class MimoV26FlashAdapter : IModelFamilyAdapter
{
    public const string FamilyId = "mimo-v2.6-flash";

    public string Id => FamilyId;
    public MosaicFamily Family => MosaicFamily.MimoV26Flash;
    public string DisplayName => "MiMo-V2.6-Flash-RL";
    public bool OwnsRuntime => true;
    public string RuntimeOwner => "mosaic";
    public IReadOnlyList<ExpertRole> PackRoles { get; } = [ExpertRole.Gate, ExpertRole.Up, ExpertRole.Down];
    public int ExpertsKeptAfterPrune => 128;
    public int RecommendedContext => 131_072;

    public ModelShape Shape { get; } = CreateOfficialShape();

    public int BitsPerWeightTenths(ExpertRole role, int layer)
    {
        // Config A in docs/MIMO.md: Q8 trunk is not an expert role.
        // Last MoE layer and every down-proj get ~3.3 bpw; remaining gate/up 2.0.
        if (role == ExpertRole.Down)
            return 33;
        if (layer == Shape.NumLayers - 1)
            return 33;
        return 20;
    }

    internal static ModelShape CreateOfficialShape()
    {
        // hybrid_layer_pattern and moe_layer_freq copied from config.json.
        int[] hybrid =
        [
            0, 1, 1, 1, 1, 0, 1, 1, 1, 1, 1, 0, 1, 1, 1, 1,
            1, 0, 1, 1, 1, 1, 1, 0, 1, 1, 1, 1, 1, 0, 1, 1,
            1, 1, 1, 0, 1, 1, 1, 1, 1, 0, 1, 1, 1, 1, 1, 0,
        ];
        int[] moe =
        [
            0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
            1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        ];

        return new ModelShape
        {
            Architecture = "MiMoV2ForCausalLM",
            TotalParameters = 309_000_000_000,
            ActiveParameters = 15_000_000_000,
            HiddenSize = 4096,
            VocabSize = 152_576,
            NumLayers = 48,
            NRoutedExperts = 256,
            NumExpertsPerTok = 8,
            MoeIntermediateSize = 2048,
            DenseIntermediateSize = 16_384,
            NumAttentionHeads = 64,
            GaKvHeads = 4,
            SwaKvHeads = 8,
            HeadDim = 192,
            VHeadDim = 128,
            SlidingWindow = 128,
            MaxContext = 1_048_576,
            HybridLayerPattern = hybrid,
            MoeLayerFreq = moe,
            ScoringFunc = "sigmoid",
            TopkMethod = "noaux_tc",
            NormTopkProb = true,
            SharedExperts = 0,
            MtpDraftTokens = 7,
            MtpLayers = 5,
        };
    }
}
