using OpenMono.Win.AgentHost;
using OpenMono.Win.Foundry;

namespace OpenMono.Win.Launcher;

public sealed record LauncherDeps(
    ICliRunner Cli,
    HttpClient Http,
    IGpuProber Gpu,
    string HomeDir,
    Func<string, string?> GetEnv,
    Func<TimeSpan, CancellationToken, Task>? Delay = null,
    int HealthAttempts = 30);

public sealed record UpOptions(
    string? ModelOverride = null,
    int Port = FoundryConstants.DefaultPort,
    bool Vision = false,
    bool Persist = true);

public sealed record UpResult(bool Ok, InferenceState? State, IReadOnlyList<string> Log, string? Error);

public sealed record DownResult(bool Stopped, string Message);

// The Windows counterpart of the bash launcher's "make sure inference is up"
// step: on Linux that is `docker compose up -d llama-server`, on Windows it is
// the Foundry Local daemon on the host. A remote llm.endpoint (or an explicit
// OPENMONO_ENDPOINT) is used as-is, exactly as the bash launcher does.
public sealed class InferenceOrchestrator
{
    private readonly LauncherDeps _deps;
    private readonly InferenceStateStore _store;

    public InferenceOrchestrator(LauncherDeps deps)
    {
        _deps = deps;
        _store = new InferenceStateStore(deps.HomeDir);
    }

    public InferenceStateStore Store => _store;

    public async Task<UpResult> UpAsync(UpOptions options, CancellationToken ct = default)
    {
        var log = new List<string>();
        var settings = HostSettings.Load(HostSettings.PathFor(_deps.HomeDir));
        if (settings.Corrupt)
        {
            return Fail(log, "~/.openmono/settings.json is corrupt. Fix the JSON or restore settings.json.bak.");
        }

        var configured = _deps.GetEnv(OmaEndpointContract.EndpointEnvVar) ?? settings.LlmEndpoint;
        if (configured is not null && !HostSettings.IsLocalEndpoint(configured))
        {
            log.Add($"llm.endpoint {configured} is remote; using it as-is (no local Foundry).");
            var remote = new InferenceState(
                InferenceState.RemoteBackend, configured, null, null, settings.LlmModel, null,
                settings.VisionEnabled, null, false, Now());
            return new UpResult(true, remote, log, null);
        }

        var cli = await new FoundryCliDetector(_deps.Cli).DetectAsync(ct);
        if (!cli.Found)
        {
            return Fail(log, "Foundry Local CLI not found. Run: .\\win\\openmono.ps1 setup "
                + "(installs it with: winget install Microsoft.FoundryLocal).");
        }

        if (!cli.IsSupported)
        {
            return Fail(log, $"Foundry Local CLI {cli.Version?.ToString() ?? "(unknown version)"} is older than "
                + $"{FoundryConstants.MinCliVersion}. Update it: winget upgrade Microsoft.FoundryLocal");
        }

        log.Add($"Foundry Local CLI {cli.Version}.");

        var gpus = await _deps.Gpu.ProbeAsync(ct);
        var vram = gpus.Where(g => g.IsNvidia).Select(g => (long?)g.DedicatedVramBytes).Max();
        log.Add(vram is { } v
            ? $"NVIDIA GPU VRAM: {Math.Round(v / (1024.0 * 1024 * 1024))} GB."
            : "No dedicated NVIDIA GPU detected: CPU-only.");

        var device = vram is null ? FoundryCatalogDevice.Cpu : FoundryCatalogDevice.Cuda;
        var view = await LoadCatalogAsync(device, ct);
        if (!view.Known)
        {
            log.Add("Foundry catalog unavailable (`foundry model list` and " + FoundryWebCatalogClient.DefaultApiUrl
                + "); `foundry model load` validates the alias.");
        }

        var selection = Select(options, vram, view, out var selectError);
        if (selection is null)
        {
            return Fail(log, selectError!);
        }

        log.Add(selection.Reason);
        if (view.Describe(selection.ModelAlias) is { FileSizeBytes: { } bytes } described)
        {
            log.Add($"{described.Alias}: about {bytes / (1024.0 * 1024 * 1024):F1} GB download on first use "
                + $"({described.Task ?? "chat"}, input {string.Join("+", described.InputModalities)}).");
        }

        var endpoint = $"http://127.0.0.1:{options.Port}";
        var service = new CliFoundryService(
            _deps.Cli, new FoundryHealthChecker(_deps.Http), _deps.Delay, _deps.HealthAttempts);
        var loadTarget = selection.CpuOnly ? CpuVariant(view, selection.ModelAlias) : null;
        if (selection.CpuOnly)
        {
            log.Add(loadTarget is not null
                ? $"Loading the generic CPU variant {loadTarget} from the Foundry catalog."
                : $"The Foundry catalog lists no CPU variant id for {selection.ModelAlias} here; loading the alias, "
                    + "and Foundry picks the variant this machine can run.");
        }

        var keepModel = options.ModelOverride is null ? settings.LlmModel : null;
        var served = await service.StartOrAdoptAsync(selection.ModelAlias, endpoint, ct, loadTarget, keepModel);
        log.AddRange(served.Log);

        if (!served.Healthy)
        {
            return Fail(log, $"Foundry Local is not serving on {served.Endpoint}. "
                + "Check `foundry server status` and `foundry server logs`, then run `openmono doctor`.");
        }

        if (served.ServedModelId is null && served.ForeignModels.Count > 0)
        {
            var target = loadTarget ?? selection.ModelAlias;
            return Fail(log, $"Foundry Local on {served.Endpoint} serves {string.Join(", ", served.ForeignModels)}, not "
                + $"{selection.ModelAlias}, and `foundry model load {target}` did not make {selection.ModelAlias} available. "
                + "The agent does not run on a model outside the OpenMono ladder. Run `foundry model load " + target
                + "`, check `foundry server logs`, then run .\\win\\openmono.ps1 start again. To use another model on purpose, "
                + "pass --model or set llm.model in settings.json.");
        }

        var previous = _store.Load();
        var probeModel = served.ServedModelId ?? selection.ModelAlias;

        bool? probeAccepted;
        string probeDetail;
        if (previous is not null
            && previous.VisionProbeAccepted is not null
            && string.Equals(previous.Endpoint, served.Endpoint, StringComparison.OrdinalIgnoreCase)
            && string.Equals(previous.ServedModelId, served.ServedModelId, StringComparison.Ordinal)
            && served.ServedModelId is not null)
        {
            probeAccepted = previous.VisionProbeAccepted;
            probeDetail = $"Reusing the earlier image probe for {probeModel}.";
            log.Add(probeDetail);
        }
        else
        {
            var probe = await new FoundryVisionProbe(_deps.Http).ProbeAsync(served.Endpoint, probeModel, ct);
            probeAccepted = probe.Accepted;
            probeDetail = probe.Detail;
        }

        var vision = WindowsVisionConfig.ApplyProbeResult(
            settings.VisionEnabled,
            new VisionProbeResult(probeAccepted == true, null, probeModel, probeDetail));
        log.Add(vision.Reason);

        // One model per machine: a model that refuses images is reported, never
        // swapped for another (a 24 GB+ card keeps qwen3.8-27b regardless).
        var refusal = probeAccepted == true
            ? "inference.vision_enabled=false is set"
            : $"{selection.ModelAlias} did not accept image input in this Foundry release";
        if (options.Vision && !vision.VisionEnabled)
        {
            return Fail(log, $"{refusal}, so a vision session cannot start. The images were not sent. "
                + "OpenMono serves one model per machine and does not load a second one for images.");
        }

        if (options.Vision)
        {
            log.Add($"Vision session: {selection.ModelAlias} accepted image input; images are sent with each turn.");
        }
        else if (!vision.VisionEnabled)
        {
            log.Add($"Images: {refusal}. The agent refuses image attachments with a warning (never drops them silently); "
                + $"OpenMono keeps serving {selection.ModelAlias} and does not load a second model.");
        }

        string? agentModel = served.ServedModelId;
        if (settings.LlmModel is not null && !options.Vision && options.ModelOverride is null)
        {
            agentModel = null;
            log.Add($"llm.model={settings.LlmModel} is set in settings.json; the agent keeps it.");
        }

        var startedByUs = served.StartedByUs
            || (previous?.StartedByUs == true
                && string.Equals(previous.Endpoint, served.Endpoint, StringComparison.OrdinalIgnoreCase));

        var state = new InferenceState(
            InferenceState.FoundryBackend,
            served.Endpoint,
            selection.ModelAlias,
            served.ServedModelId,
            agentModel,
            selection.ContextTokens,
            vision.VisionEnabled,
            probeAccepted,
            startedByUs,
            Now())
        {
            ToolCalling = agentModel is null && settings.LlmModel is not null
                ? null
                : view.Describe(selection.ModelAlias)?.SupportsToolCalling,
        };

        if (options.Persist)
        {
            _store.Save(state);
        }

        return new UpResult(true, state, log, null);
    }

    public async Task<DownResult> DownAsync(bool force, CancellationToken ct = default)
    {
        var state = _store.Load();
        if (state?.Backend == InferenceState.RemoteBackend)
        {
            _store.Delete();
            return new DownResult(false, "Inference is remote; nothing local to stop.");
        }

        if (state?.StartedByUs != true && !force)
        {
            return new DownResult(false,
                "Foundry Local was not started by OpenMono; leaving it running. Use --force to stop it anyway.");
        }

        var service = new CliFoundryService(_deps.Cli, new FoundryHealthChecker(_deps.Http), _deps.Delay, _deps.HealthAttempts);
        CliResult result;
        try
        {
            result = await service.StopAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DownResult(false, $"Could not run foundry server stop: {ex.Message}");
        }

        _store.Delete();
        return result.Succeeded
            ? new DownResult(true, "Foundry Local daemon stopped.")
            : new DownResult(false, "foundry server stop failed: " + (result.StandardOutput + result.StandardError).Trim());
    }

    // `foundry model list` first. On a GPU machine the public catalog API is
    // read only when the local list is empty, and otherwise from its cache for
    // size and modality notes, so a normal start makes no extra network call.
    // A CPU-only machine reads the CPU catalog (cached for an hour) for the
    // generic CPU variant id, which `foundry model list` does not report.
    private async Task<FoundryCatalogView> LoadCatalogAsync(FoundryCatalogDevice device, CancellationToken ct)
    {
        var local = await new FoundryCatalogClient(_deps.Cli).ListModelsAsync(ct);
        var web = await FoundryWebCatalogClient.FromEnvironment(_deps.Http, _deps.GetEnv, _deps.HomeDir)
            .ListAsync(device, cacheOnly: local.Count > 0 && device != FoundryCatalogDevice.Cpu, ct: ct);
        return new FoundryCatalogView(local, web);
    }

    public static string? CpuVariant(FoundryCatalogView view, string alias) =>
        view.Describe(alias) is { VariantId: { Length: > 0 } id } model
            && string.Equals(model.ExecutionProvider, FoundryConstants.CpuExecutionProvider, StringComparison.OrdinalIgnoreCase)
            ? id
            : null;

    private static ModelSelection? Select(UpOptions options, long? vram, FoundryCatalogView view, out string? error)
    {
        error = null;
        if (options.ModelOverride is { } requested)
        {
            var alias = view.Known ? view.Resolve(requested) : requested;
            if (alias is null)
            {
                error = $"--model {requested} is not in the Foundry catalog ({view.Source}); nothing was downloaded. "
                    + $"See {FoundryWebCatalogClient.ModelsPageUrl} or run: .\\win\\openmono.ps1 catalog";
                return null;
            }

            var ctx = vram >= FoundryConstants.FullContextVramBytes
                ? FoundryConstants.MaxContextTokens
                : FoundryConstants.StandardContextTokens;
            var cpuNote = vram is null && FoundryModelLadder.CpuOnly.All(r => !string.Equals(r.Alias, alias, StringComparison.OrdinalIgnoreCase))
                ? $" No dedicated NVIDIA GPU: it runs on the CPU, where OpenMono would pick {FoundryConstants.CpuModelAlias}."
                : "";
            return new ModelSelection(alias, ctx, false, $"Model override: {alias}.{cpuNote}");
        }

        // An unreadable catalog must not demote a 24 GB box: ask Foundry for
        // the ladder alias directly and let `model load` validate it.
        var selection = ModelSelector.Select(vram, view.Known ? view.Resolve : alias => alias);
        if (selection is null)
        {
            error = NothingInCatalog(vram, view);
        }

        return selection;
    }

    public static string NothingInCatalog(long? vram, FoundryCatalogView view)
    {
        var rung = ModelSelector.RungFor(vram);
        if (ReferenceEquals(rung, FoundryModelLadder.Target))
        {
            return $"{FoundryConstants.TargetModelAlias} is not in the Foundry catalog ({view.Source}). A 24 GB+ card serves only "
                + $"{FoundryConstants.TargetModelAlias}, so nothing was downloaded. Update Foundry Local "
                + $"(winget upgrade Microsoft.FoundryLocal) and check {FoundryWebCatalogClient.ModelsPageUrl}.";
        }

        if (vram is null)
        {
            return $"No dedicated NVIDIA GPU, so this machine serves only {string.Join(" or ", FoundryModelLadder.CpuOnly.Select(r => r.Alias))} "
                + $"on the CPU, and neither is in the Foundry catalog ({view.Source}); nothing was downloaded. "
                + $"Check {FoundryWebCatalogClient.ModelsPageUrl}.";
        }

        var candidates = FoundryModelLadder.Text.SkipWhile(r => !ReferenceEquals(r, rung)).Select(r => r.Alias);
        return $"None of the models for this card ({string.Join(", ", candidates)}) are in the Foundry catalog "
            + $"({view.Source}); nothing was downloaded. Check {FoundryWebCatalogClient.ModelsPageUrl}.";
    }

    private static UpResult Fail(List<string> log, string error) => new(false, null, log, error);

    private static string Now() => DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
}
