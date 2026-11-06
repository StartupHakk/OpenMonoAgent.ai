using OpenMono.Win.Foundry;

namespace OpenMono.Win.Launcher;

public sealed record RungStatus(LadderRung Rung, bool? InPublicCatalog, bool? InLocalCatalog, FoundryCatalogModel? Metadata);

public sealed record CatalogCheckResult(
    FoundryCatalogView View,
    IReadOnlyList<RungStatus> Rungs,
    GpuInfo? Gpu,
    LadderRung PreferredRung,
    ModelSelection? Pick,
    string? Error,
    FoundryCatalogView PickView,
    string? CpuVariant)
{
    public IReadOnlyList<string> MissingFromPublic =>
        Rungs.Where(r => r.InPublicCatalog == false).Select(r => r.Rung.Alias).ToList();

    // Nothing this machine may serve is in a catalog that did answer.
    public bool Unusable => PickView.Known && Pick is null;
}

// Validates the Windows model ladder against the public Foundry catalog
// (https://foundrylocal.ai/api/models) and, when Foundry Local is installed,
// against `foundry model list`, then names the model this machine will
// serve. The ladder table is always checked against the CUDA catalog; a
// machine without a dedicated NVIDIA GPU picks from the CPU catalog, which
// also carries the generic CPU variant id it loads. Shared by
// `openmono catalog check`, `doctor`, and setup.
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

        var pickView = view;
        if (gpu is null)
        {
            var cpuWeb = await FoundryWebCatalogClient.FromEnvironment(deps.Http, deps.GetEnv, deps.HomeDir)
                .ListAsync(FoundryCatalogDevice.Cpu, refresh, ct: ct);
            pickView = new FoundryCatalogView(local, cpuWeb);
        }

        var pick = pickView.Known ? ModelSelector.Select(vram, pickView.Resolve) : null;
        var error = pickView.Known && pick is null ? InferenceOrchestrator.NothingInCatalog(vram, pickView) : null;
        var cpuVariant = pick is { CpuOnly: true } ? InferenceOrchestrator.CpuVariant(pickView, pick.ModelAlias) : null;

        return new CatalogCheckResult(view, rungs, gpu, ModelSelector.RungFor(vram), pick, error, pickView, cpuVariant);
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
