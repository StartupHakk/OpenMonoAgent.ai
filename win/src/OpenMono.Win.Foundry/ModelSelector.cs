namespace OpenMono.Win.Foundry;

// Which model the host should serve, and with what context window.
// Pure function of detected VRAM plus catalog matches, so tests need no GPU.
public sealed record ModelSelection(
    string ModelAlias,
    int ContextTokens,
    bool IsTargetModel,
    string Reason);

public static class ModelSelector
{
    private static long GiB(long bytes) => (long)Math.Round(bytes / (1024.0 * 1024 * 1024));

    public static ModelSelection Select(
        long? dedicatedVramBytes,
        string? targetCatalogMatch,
        string? fallbackCatalogMatch = null)
    {
        var fallback = fallbackCatalogMatch ?? FoundryConstants.FallbackModelAlias;

        if (targetCatalogMatch is not null && dedicatedVramBytes is { } vram)
        {
            if (vram >= FoundryConstants.FullContextVramBytes)
            {
                return new ModelSelection(
                    targetCatalogMatch,
                    FoundryConstants.MaxContextTokens,
                    true,
                    $"Detected {GiB(vram)} GB VRAM: full {FoundryConstants.MaxContextTokens} token context.");
            }

            if (vram >= FoundryConstants.RequiredVramBytes27B)
            {
                return new ModelSelection(
                    targetCatalogMatch,
                    FoundryConstants.StandardContextTokens,
                    true,
                    $"Detected {GiB(vram)} GB VRAM: {FoundryConstants.StandardContextTokens} token context.");
            }

            return new ModelSelection(
                fallback,
                FoundryConstants.StandardContextTokens,
                false,
                $"Detected {GiB(vram)} GB VRAM, below the 24 GB floor for "
                    + $"{FoundryConstants.TargetModelAlias}. Using {fallback} instead.");
        }

        if (targetCatalogMatch is not null)
        {
            return new ModelSelection(
                targetCatalogMatch,
                FoundryConstants.StandardContextTokens,
                true,
                "VRAM size unknown; serving the target model with the standard 64K context.");
        }

        return new ModelSelection(
            fallback,
            FoundryConstants.StandardContextTokens,
            false,
            $"Target model {FoundryConstants.TargetModelAlias} not found in the Foundry catalog. Using {fallback}.");
    }
}
