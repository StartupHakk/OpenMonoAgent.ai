namespace OpenMono.Win.Foundry;

// Which model the host serves, and with what context window.
// SkippedAliases are ladder aliases passed over because the catalog lacks them.
public sealed record ModelSelection(
    string ModelAlias,
    int ContextTokens,
    bool IsTargetModel,
    string Reason,
    IReadOnlyList<string>? SkippedAliases = null);

// Walks FoundryModelLadder for the detected VRAM. Pure: `resolve` maps a
// ladder alias to its catalog spelling, or null when the catalog lacks it,
// so tests need neither a GPU nor a network. Returns null when nothing usable
// is in the catalog; the caller must then fail loudly instead of downloading
// something else.
public static class ModelSelector
{
    private static long GiB(long bytes) => (long)Math.Round(bytes / (1024.0 * 1024 * 1024));

    // The rung the VRAM class alone points at. Unknown VRAM (no NVIDIA GPU
    // detected) keeps the target model, as before the ladder existed.
    public static LadderRung RungFor(long? dedicatedVramBytes) =>
        dedicatedVramBytes is { } vram
            ? FoundryModelLadder.Text.First(r => vram >= r.MinVramBytes)
            : FoundryModelLadder.Target;

    public static ModelSelection? Select(long? dedicatedVramBytes, Func<string, string?> resolve)
    {
        var preferred = RungFor(dedicatedVramBytes);

        // A 24 GB+ card serves qwen3.8-27b or nothing: never a smaller model.
        if (ReferenceEquals(preferred, FoundryModelLadder.Target))
        {
            var target = resolve(preferred.Alias);
            if (target is null)
            {
                return null;
            }

            var context = ContextFor(preferred, dedicatedVramBytes, isTarget: true);
            return new ModelSelection(target, context, true, TargetReason(dedicatedVramBytes, target, context), []);
        }

        var skipped = new List<string>();
        var ladder = FoundryModelLadder.Text;
        for (var i = IndexOf(preferred); i < ladder.Count; i++)
        {
            var rung = ladder[i];
            var match = resolve(rung.Alias);
            if (match is null)
            {
                skipped.Add(rung.Alias);
                continue;
            }

            var context = ContextFor(rung, dedicatedVramBytes, isTarget: false);
            var missing = skipped.Count == 0
                ? ""
                : $" Not in the Foundry catalog: {string.Join(", ", skipped)}; using the next smaller model.";
            return new ModelSelection(
                match,
                context,
                false,
                $"Detected {GiB(dedicatedVramBytes!.Value)} GB VRAM, below the 24 GB floor for {FoundryConstants.TargetModelAlias}. "
                    + $"Using {match} ({rung.VramClass} class, text and images) with {context} token context.{missing}",
                skipped);
        }

        return null;
    }

    private static int IndexOf(LadderRung rung)
    {
        var ladder = FoundryModelLadder.Text;
        for (var i = 0; i < ladder.Count; i++)
        {
            if (ReferenceEquals(ladder[i], rung))
            {
                return i;
            }
        }

        return 0;
    }

    // Only the 24 GB coding model gets the full window, and only on 32 GB
    // class cards; the smaller models stay at the standard 64K.
    private static int ContextFor(LadderRung rung, long? vram, bool isTarget)
    {
        var cap = isTarget && vram >= FoundryConstants.FullContextVramBytes
            ? FoundryConstants.MaxContextTokens
            : FoundryConstants.StandardContextTokens;
        return Math.Min(cap, rung.MaxContextTokens);
    }

    private static string TargetReason(long? vram, string alias, int context) =>
        vram switch
        {
            null => $"VRAM size unknown; serving {alias} with the standard {context} token context.",
            _ when vram >= FoundryConstants.FullContextVramBytes =>
                $"Detected {GiB(vram.Value)} GB VRAM: {alias} with the full {context} token context.",
            _ => $"Detected {GiB(vram.Value)} GB VRAM: {alias} with {context} token context.",
        };
}
