namespace OpenMono.Win.Foundry;

// One step of the model ladder: the catalog alias, the smallest VRAM class it
// is picked for, the context window the model supports, and whether the
// catalog lists it as vision-language-chat (text plus image input).
public sealed record LadderRung(
    string Alias,
    long MinVramBytes,
    int MaxContextTokens,
    string VramClass,
    bool VisionLanguage);

// The one model each machine serves, largest first. Every alias was checked
// against the Foundry Local CUDA catalog (https://foundrylocal.ai/models,
// queried through https://foundrylocal.ai/api/models) on 2026-11-05, and is
// resolved against the live catalog again before anything is downloaded.
//
// 24 GB and up always serve qwen3.8-27b: no step-down and no second model,
// for vision or anything else. Below the 24 GB floor the Qwen3.5
// vision-language models serve text and images from the same model; if the
// preferred one is missing from the catalog, the next smaller Qwen3.5 rung is
// used. VRAM classes start 1 GiB below their nominal size because cards report
// a little less than marketed (see FoundryConstants).
//
// No dedicated NVIDIA GPU means Foundry runs on the CPU: qwen3.5-2b (its
// generic CPU variant when the catalog lists one), never qwen3.8-27b or the
// larger Qwen3.5 rungs. qwen3.5-0.8b is the only step down from there.
public static class FoundryModelLadder
{
    private const long GiB = 1024L * 1024 * 1024;

    public const string CatalogCheckedOn = "2026-11-05";

    public static readonly IReadOnlyList<LadderRung> Text =
    [
        new(FoundryConstants.TargetModelAlias, FoundryConstants.RequiredVramBytes27B, FoundryConstants.MaxContextTokens, "24 GB", false),
        new("qwen3.5-9b", 11 * GiB, 262144, "12 GB", true),
        new("qwen3.5-4b", 7 * GiB, 262144, "8 GB", true),
        new("qwen3.5-2b", 3 * GiB, 262144, "4 GB", true),
        new("qwen3.5-0.8b", 0, 262144, "under 4 GB", true),
    ];

    public static readonly IReadOnlyList<LadderRung> CpuOnly =
    [
        new(FoundryConstants.CpuModelAlias, 0, 262144, "no GPU (CPU)", true),
        new("qwen3.5-0.8b", 0, 262144, "no GPU (CPU)", true),
    ];

    public static LadderRung Target => Text[0];

    public static LadderRung Cpu => CpuOnly[0];

    public static IEnumerable<string> AllAliases => Text.Select(r => r.Alias);

    public static LadderRung? Find(string alias) =>
        Text.FirstOrDefault(r => string.Equals(r.Alias, alias, StringComparison.OrdinalIgnoreCase));
}
