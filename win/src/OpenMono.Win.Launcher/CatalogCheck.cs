using OpenMono.Win.Foundry;

namespace OpenMono.Win.Launcher;

public sealed record RungStatus(LadderRung Rung, bool? InPublicCatalog, bool? InLocalCatalog, FoundryCatalogModel? Metadata);

public sealed record CatalogCheckResult(
    FoundryCatalogView View,
    IReadOnlyList<RungStatus> Rungs,
    GpuInfo? Gpu,
    LadderRung PreferredRung,
    ModelSelection? Pick,
    string? Error)
{
    public IReadOnlyList<string> MissingFromPublic =>
        Rungs.Where(r => r.InPublicCatalog == false).Select(r => r.Rung.Alias).ToList();

    // Nothing this machine may serve is in a catalog that did answer.
    public bool Unusable => View.Known && Pick is null;
}

// Validates the Windows model ladder against the public Foundry catalog
// (https://foundrylocal.ai/api/models) and, when Foundry Local is installed,
// against `foundry model list`, then names the model this machine will
// serve. Shared by `openmono catalog check`, `doctor`, and setup.
public static class CatalogCheck
{
    public static async Task<CatalogCheckResult> RunAsync(LauncherDeps deps, bool refresh = false, CancellationToken ct = default)
    {
        var web = await FoundryWebCatalogClient.FromEnvironment(deps.Http, deps.GetEnv, deps.HomeDir)
            .ListAsync(FoundryCatalogDevice.Cuda, refresh, ct: ct);
        var local = await new FoundryCatalogClient(deps.Cli).ListModelsAsync(ct);
        var view = new FoundryCatalogView(local, web);

        var webAliases = web.Models.Select(m => m.Alias).ToList();
        var rungs = FoundryModelLadder.Text.Select(r => new RungStatus(
            r,
            web.Models.Count > 0 ? FoundryAliasResolver.Resolve(r.Alias, webAliases) is not null : null,
            local.Count > 0 ? FoundryAliasResolver.Resolve(r.Alias, local) is not null : null,
            view.Describe(r.Alias))).ToList();

        var gpus = await deps.Gpu.ProbeAsync(ct);
        var gpu = gpus.Where(g => g.IsNvidia).OrderByDescending(g => g.DedicatedVramBytes).FirstOrDefault();
        var vram = gpu?.DedicatedVramBytes;
        var pick = view.Known ? ModelSelector.Select(vram, view.Resolve) : null;
        var error = view.Known && pick is null ? InferenceOrchestrator.NothingInCatalog(vram, view) : null;

        return new CatalogCheckResult(view, rungs, gpu, ModelSelector.RungFor(vram), pick, error);
    }

    public static string Size(long? bytes) =>
        bytes is { } b ? $"about {b / (1024.0 * 1024 * 1024):F1} GB" : "size unknown";

    public static string PublicSummary(WebCatalogResult web)
    {
        if (!web.Ok)
        {
            return $"{web.Source} unreachable ({web.Error}).";
        }

        var age = web.FromCache && web.FetchedUtc is { } fetched
            ? $", cached {(int)Math.Max(0, (DateTimeOffset.UtcNow - fetched).TotalMinutes)} min ago"
            : "";
        var note = web.Error is null ? "" : $" ({web.Error})";
        return $"{web.Source}: {web.Models.Count} CUDA models{age}{note}.";
    }
}
