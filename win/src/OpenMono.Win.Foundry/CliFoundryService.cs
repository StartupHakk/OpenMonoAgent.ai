namespace OpenMono.Win.Foundry;

// Exact CLI command shapes the host shells out to. Kept as pure data so tests
// pin the contract and first Windows runs verify flags against `foundry --help`.
// Minimal subcommand forms are used on purpose: flag spelling is the part most
// likely to drift between Foundry releases.
public static class FoundryCliCommands
{
    public static (string File, string Args) ModelLoad(string alias) =>
        (FoundryCliDetector.CliExecutableName, $"model load {alias}");

    public static (string File, string Args) ServerStart(int port) =>
        (FoundryCliDetector.CliExecutableName, $"server start --port {port}");

    public static (string File, string Args) ServerStatus() =>
        (FoundryCliDetector.CliExecutableName, "server status");

    public static (string File, string Args) ModelList() =>
        (FoundryCliDetector.CliExecutableName, "model list");
}

// Drives the Foundry CLI to load a model and serve it. M1 scope: load plus
// start/status; download resume, progress, and disk preflight arrive with M3.
public sealed record FoundryServiceResult(
    string Endpoint,
    string ModelAlias,
    bool AdoptedExisting,
    IReadOnlyList<string> Log);

public sealed class CliFoundryService
{
    private readonly ICliRunner _runner;
    private readonly FoundryHealthChecker _health;

    public CliFoundryService(ICliRunner runner, FoundryHealthChecker health)
    {
        _runner = runner;
        _health = health;
    }

    public async Task<FoundryServiceResult> StartOrAdoptAsync(
        string modelAlias,
        string endpoint,
        CancellationToken ct = default)
    {
        var log = new List<string>();

        var current = await _health.CheckAsync(endpoint, ct);
        var plan = FoundryEndpointDecider.Decide(current, endpoint, modelAlias);
        log.Add(plan.Reason);

        if (plan.Decision == EndpointDecision.AdoptExisting)
        {
            return new FoundryServiceResult(endpoint, modelAlias, true, log);
        }

        var (loadFile, loadArgs) = FoundryCliCommands.ModelLoad(modelAlias);
        log.Add($"Running: {loadFile} {loadArgs}");
        var load = await _runner.RunAsync(loadFile, loadArgs, ct);
        log.Add(load.Succeeded ? "Model load accepted." : $"Model load output: {(load.StandardOutput + load.StandardError).Trim()}");

        var (startFile, startArgs) = FoundryCliCommands.ServerStart(FoundryConstants.DefaultPort);
        log.Add($"Running: {startFile} {startArgs}");
        var start = await _runner.RunAsync(startFile, startArgs, ct);
        log.Add(start.Succeeded ? "Server start accepted." : $"Server start output: {(start.StandardOutput + start.StandardError).Trim()}");

        var after = await _health.CheckAsync(endpoint, ct);
        log.Add(after.Reachable
            ? $"Server healthy on {endpoint}."
            : $"Server not yet healthy on {endpoint}: {after.Error ?? "no detail"}. Check `foundry server status`.");

        return new FoundryServiceResult(endpoint, modelAlias, false, log);
    }
}
