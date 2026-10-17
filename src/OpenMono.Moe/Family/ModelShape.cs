namespace OpenMono.Moe.Family;

/// <summary>
/// Architecture constants a family adapter publishes so the shared runtime
/// can size arenas, KV, and toy forwards without hard-coding MiMo.
/// </summary>
public sealed record ModelShape
{
    public required string Architecture { get; init; }
    public required long TotalParameters { get; init; }
    public required long ActiveParameters { get; init; }
    public required int HiddenSize { get; init; }
    public required int VocabSize { get; init; }
    public required int NumLayers { get; init; }
    public required int NRoutedExperts { get; init; }
    public required int NumExpertsPerTok { get; init; }
    public required int MoeIntermediateSize { get; init; }
    public required int DenseIntermediateSize { get; init; }
    public required int NumAttentionHeads { get; init; }
    public required int GaKvHeads { get; init; }
    public required int SwaKvHeads { get; init; }
    public required int HeadDim { get; init; }
    public required int VHeadDim { get; init; }
    public required int SlidingWindow { get; init; }
    public required int MaxContext { get; init; }
    public required int[] HybridLayerPattern { get; init; }
    public required int[] MoeLayerFreq { get; init; }
    public required string ScoringFunc { get; init; }
    public required string TopkMethod { get; init; }
    public required bool NormTopkProb { get; init; }
    public int SharedExperts { get; init; }
    public int MtpDraftTokens { get; init; }
    public int MtpLayers { get; init; }

    public int GaLayers => HybridLayerPattern.Count(p => p == 0);
    public int SwaLayers => HybridLayerPattern.Count(p => p == 1);
    public int MoeLayers => MoeLayerFreq.Count(f => f != 0);
    public int DenseLayers => MoeLayerFreq.Count(f => f == 0);

    public bool IsGlobalAttention(int layer) =>
        layer >= 0 && layer < HybridLayerPattern.Length && HybridLayerPattern[layer] == 0;

    public bool IsMoeLayer(int layer) =>
        layer >= 0 && layer < MoeLayerFreq.Length && MoeLayerFreq[layer] != 0;
}
