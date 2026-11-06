namespace OpenMono.Win.Foundry;

// Exact CLI command shapes the host shells out to, pinned by tests. Verified
// against the Foundry Local CLI reference (0.10+ "server" command group,
// which replaced the older "service" group): `server start --port N
// --idle-timeout 0` pins the port and keeps the daemon up, `model load`
// downloads on first use and loads into the daemon, and most commands accept
// `--output json`.
public static class FoundryCliCommands
{
    public static (string File, string Args) ModelLoad(string alias) =>
        (FoundryCliDetector.CliExecutableName, $"model load {alias}");

    public static (string File, string Args) ModelUnload(string modelId) =>
        (FoundryCliDetector.CliExecutableName, $"model unload {modelId}");

    public static (string File, string Args) ServerStart(int port) =>
        (FoundryCliDetector.CliExecutableName, $"server start --port {port} --idle-timeout 0");

    public static (string File, string Args) ServerStop() =>
        (FoundryCliDetector.CliExecutableName, "server stop");

    public static (string File, string Args) ServerStatus() =>
        (FoundryCliDetector.CliExecutableName, "server status");

    public static (string File, string Args) ModelList() =>
        (FoundryCliDetector.CliExecutableName, "model list");

    public static (string File, string Args) ModelListJson() =>
        (FoundryCliDetector.CliExecutableName, "model list --output json");
}

public sealed record FoundryServiceResult(
    string Endpoint,
    string ModelAlias,
    string? ServedModelId,
    bool AdoptedExisting,
    bool StartedByUs,
    bool Healthy,
    IReadOnlyList<string> Log)
{
    // Models the adopted daemon served that are not the selected model (for
    // example a DeepSeek-R1-Distill OpenVINO model a user loaded earlier).
    public IReadOnlyList<string> ForeignModels { get; init; } = [];

    // Foreign models unloaded after the selected model was serving.
    public IReadOnlyList<string> ReplacedModels { get; init; } = [];
}

// Adopt-or-start for the Foundry daemon, then make sure the model is loaded.
// Order matters: the server is started on the pinned port before the model
// load, because a load against a stopped daemon would auto-start it on a
// different port. A daemon the user already runs (on 7474 or elsewhere, per
// `foundry server status`) is adopted, never duplicated, but the model it
// serves is not: anything other than the selected model (or a model the user
// pinned with llm.model) is replaced by the selected model, so the agent never
// runs on whatever Foundry happened to have loaded.
public sealed class CliFoundryService
{
    private readonly ICliRunner _runner;
    private readonly FoundryHealthChecker _health;
    private readonly FoundryCliDetector _detector;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly int _healthAttempts;

    public CliFoundryService(
        ICliRunner runner,
        FoundryHealthChecker health,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        int healthAttempts = 30)
    {
        _runner = runner;
        _health = health;
        _detector = new FoundryCliDetector(runner);
        _delay = delay ?? Task.Delay;
        _healthAttempts = Math.Max(1, healthAttempts);
    }

    // loadTarget: what `model load` gets when it differs from the alias (a
    // catalog variant id such as qwen3.5-2b-generic-cpu:3). keepModel: a model
    // the user chose (settings.json llm.model) that is never unloaded.
    public async Task<FoundryServiceResult> StartOrAdoptAsync(
        string modelAlias,
        string endpoint,
        CancellationToken ct = default,
        string? loadTarget = null,
        string? keepModel = null)
    {
        var log = new List<string>();
        var adopted = false;
        var startedByUs = false;

        var current = await _health.CheckAsync(endpoint, ct);
        var plan = FoundryEndpointDecider.Decide(current, endpoint, modelAlias);
        log.Add(plan.Reason);

        if (plan.Decision == EndpointDecision.AdoptExisting)
        {
            adopted = true;
        }
        else
        {
            var status = await _detector.GetServerStatusAsync(ct);
            if (status.Running && status.Url is { } otherUrl
                && !string.Equals(OmaEndpointContract.TrimRoot(otherUrl), OmaEndpointContract.TrimRoot(endpoint), StringComparison.OrdinalIgnoreCase))
            {
                var other = await _health.CheckAsync(OmaEndpointContract.TrimRoot(otherUrl), ct);
                if (other.Reachable)
                {
                    endpoint = OmaEndpointContract.TrimRoot(otherUrl);
                    current = other;
                    adopted = true;
                    log.Add($"Foundry daemon already serving on {endpoint} (started outside OpenMono); adopting it instead of binding a second server.");
                }
            }

            if (!adopted)
            {
                var port = PortOf(endpoint);
                var (startFile, startArgs) = FoundryCliCommands.ServerStart(port);
                log.Add($"Running: {startFile} {startArgs}");
                var start = await _runner.RunAsync(startFile, startArgs, ct);
                log.Add(start.Succeeded
                    ? "Server start accepted."
                    : $"Server start output: {Combined(start)}");
                startedByUs = start.Succeeded;

                current = await WaitHealthyAsync(endpoint, ct);
                log.Add(current.Reachable
                    ? $"Server healthy on {endpoint}."
                    : $"Server not healthy on {endpoint}: {current.Error ?? "no detail"}. Check `foundry server status` and `foundry server logs`.");
            }
        }

        bool Wanted(string id) =>
            FoundryModelIdResolver.ResolveServedId(modelAlias, [id]) is not null
            || (loadTarget is not null && FoundryModelIdResolver.ResolveServedId(loadTarget, [id]) is not null)
            || (keepModel is not null && FoundryModelIdResolver.ResolveServedId(keepModel, [id]) is not null);

        var foreign = current.Models.Where(id => !Wanted(id)).ToList();
        if (foreign.Count > 0)
        {
            log.Add($"Foundry is serving {string.Join(", ", foreign)}, which is not {modelAlias} (the OpenMono model for this machine); "
                + $"switching to {modelAlias}.");
        }

        var servedId = FoundryModelIdResolver.ResolveServedId(modelAlias, current.Models)
            ?? (loadTarget is null ? null : FoundryModelIdResolver.ResolveServedId(loadTarget, current.Models));
        if (servedId is null && current.Reachable)
        {
            var (loadFile, loadArgs) = FoundryCliCommands.ModelLoad(loadTarget ?? modelAlias);
            log.Add($"Running: {loadFile} {loadArgs} (first run downloads the model; this can take a while)");
            var load = await _runner.RunAsync(loadFile, loadArgs, ct);
            log.Add(load.Succeeded ? "Model load accepted." : $"Model load output: {Combined(load)}");

            current = await _health.CheckAsync(endpoint, ct);
            servedId = FoundryModelIdResolver.ResolveServedId(modelAlias, current.Models)
                ?? (loadTarget is null ? null : FoundryModelIdResolver.ResolveServedId(loadTarget, current.Models));
        }

        var replaced = new List<string>();
        if (servedId is not null && foreign.Count > 0)
        {
            foreach (var id in current.Models.Where(id => !Wanted(id)))
            {
                var (unloadFile, unloadArgs) = FoundryCliCommands.ModelUnload(id);
                log.Add($"Running: {unloadFile} {unloadArgs}");
                var unload = await TryRunAsync(unloadFile, unloadArgs, ct);
                if (unload is not { Succeeded: true })
                {
                    log.Add($"Could not unload {id}: {(unload is null ? "command failed" : Combined(unload))}. "
                        + $"The agent still uses {servedId}.");
                }
            }

            current = await _health.CheckAsync(endpoint, ct);
            replaced.AddRange(foreign);
            log.Add($"Replaced the previously served model {string.Join(", ", foreign)} with {servedId}.");
        }

        log.Add(servedId is not null
            ? $"Serving {modelAlias} as model id {servedId}."
            : $"{modelAlias} is not listed by {endpoint}/v1/models yet; the agent will auto-detect the served model.");

        return new FoundryServiceResult(endpoint, modelAlias, servedId, adopted, startedByUs, current.Reachable, log)
        {
            ForeignModels = servedId is null ? current.Models.Where(id => !Wanted(id)).ToList() : [],
            ReplacedModels = replaced,
        };
    }

    private async Task<CliResult?> TryRunAsync(string file, string args, CancellationToken ct)
    {
        try
        {
            return await _runner.RunAsync(file, args, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public async Task<CliResult> StopAsync(CancellationToken ct = default)
    {
        var (file, args) = FoundryCliCommands.ServerStop();
        return await _runner.RunAsync(file, args, ct);
    }

    private async Task<FoundryHealth> WaitHealthyAsync(string endpoint, CancellationToken ct)
    {
        FoundryHealth health = new(false, [], false, "not checked");
        for (var attempt = 0; attempt < _healthAttempts; attempt++)
        {
            health = await _health.CheckAsync(endpoint, ct);
            if (health.Reachable)
            {
                return health;
            }

            await _delay(TimeSpan.FromSeconds(1), ct);
        }

        return health;
    }

    private static int PortOf(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Port > 0
            ? uri.Port
            : FoundryConstants.DefaultPort;

    private static string Combined(CliResult result) =>
        (result.StandardOutput + result.StandardError).Trim();
}
