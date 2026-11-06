namespace OpenMono.Win.Inference;

public sealed record RunningServer(int? Pid, string Fingerprint);

public sealed record ServeResult(
    bool Ok,
    int? Pid,
    bool StartedByUs,
    IReadOnlyList<string> Log,
    string? Error,
    bool Crashed = false);

// Brings llama-server up on the host the way `docker compose up -d
// llama-server` does on Linux, without ever running two servers on the port:
//   - our server from the last run, same flags, healthy: reuse it;
//   - a healthy server on the port that serves this alias: adopt it;
//   - our server with other flags (other model, build, context): restart it;
//   - someone else's server with another model: stop with a clear message,
//     since llama-server serves exactly the model it was started with.
public sealed class LlamaServerService
{
    // One check a second: ten minutes covers a 16.5 GB GGUF read from a slow
    // disk. A server that exits ends the wait at once.
    public const int DefaultHealthAttempts = 600;

    private readonly IServerProcessHost _host;
    private readonly ServerHealthChecker _health;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly int _healthAttempts;

    public LlamaServerService(
        IServerProcessHost host,
        ServerHealthChecker health,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        int healthAttempts = DefaultHealthAttempts)
    {
        _host = host;
        _health = health;
        _delay = delay ?? Task.Delay;
        _healthAttempts = Math.Max(1, healthAttempts);
    }

    public async Task<ServeResult> StartOrAdoptAsync(
        InstalledBuild build,
        LlamaServerConfig config,
        RunningServer? previous,
        string installRoot,
        CancellationToken ct = default)
    {
        var log = new List<string>();
        var endpoint = $"http://127.0.0.1:{config.Port}";
        var fingerprint = LlamaServerArgs.Fingerprint(build.Id, config);
        var ownPid = previous?.Pid is { } p && _host.IsRunning(p) ? p : (int?)null;
        var health = await _health.CheckAsync(endpoint, ct);

        if (health.Reachable && ownPid is not null && previous!.Fingerprint == fingerprint)
        {
            var waited = await WaitReadyAsync(endpoint, ownPid, ct);
            if (waited.Ready && waited.ListsModel(config.Alias))
            {
                log.Add($"llama-server (pid {ownPid}) already serves {config.Alias} on {endpoint}.");
                return new ServeResult(true, ownPid, true, log, null);
            }
        }

        var ours = _host.FindUnder(installRoot).ToList();
        if (ownPid is { } known && !ours.Contains(known))
        {
            ours.Add(known);
        }

        // Our own server keeps running only with the same flags; anyone else's
        // server is adopted whenever it serves the alias.
        var sameFlags = previous is null || previous.Fingerprint == fingerprint;
        if (health.Ready && health.ListsModel(config.Alias) && (sameFlags || ours.Count == 0))
        {
            var mine = ours.Count > 0;
            log.Add(mine
                ? $"llama-server already serves {config.Alias} on {endpoint}; reusing it."
                : $"A server on {endpoint} already serves {config.Alias}; adopting it (OpenMono did not start it and will not stop it).");
            return new ServeResult(true, ours.FirstOrDefault() is var pid && pid > 0 ? pid : null, mine, log, null);
        }

        if (ours.Count > 0)
        {
            foreach (var pid in ours)
            {
                _host.Stop(pid);
            }

            log.Add(health.Models.Count > 0 && !health.ListsModel(config.Alias)
                ? $"Restarting llama-server: it served {string.Join(", ", health.Models)}, this machine needs {config.Alias}."
                : "Restarting llama-server with the new settings.");
            health = await WaitPortFreeAsync(endpoint, ct);
        }

        if (health.Reachable)
        {
            return new ServeResult(false, null, false, log, PortTaken(config.Port, health, config.Alias));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(config.LogFile)!);
        int started;
        try
        {
            started = _host.Start(build.ServerPath, LlamaServerArgs.Build(config), build.Directory);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ServeResult(false, null, false, log, $"Could not start {build.ServerPath}: {ex.Message}", Crashed: true);
        }

        log.Add($"Started llama-server {build.Id} (pid {started}): {config.Alias}, context {config.ContextTokens}, "
            + $"kv {config.Kv}{(config.MmprojPath is null ? "" : ", vision projector")}, log {config.LogFile}.");

        var ready = await WaitReadyAsync(endpoint, started, ct);
        if (ready.Ready)
        {
            return new ServeResult(true, started, true, log, null);
        }

        // Only a build that exits falls back to the next backend; one that is
        // still loading would load the same model again, slower.
        var crashed = !_host.IsRunning(started);
        if (!crashed)
        {
            _host.Stop(started);
        }

        return new ServeResult(false, null, false, log,
            crashed
                ? $"llama-server {build.Id} exited while loading {config.Alias}. Last log lines:{Environment.NewLine}{Tail(config.LogFile)}"
                : $"llama-server {build.Id} did not become healthy on {endpoint} in time ({ready.Error ?? "still loading"}); it was stopped. See {config.LogFile}.",
            Crashed: crashed);
    }

    public static string PortTaken(int port, ServerHealth health, string alias)
    {
        var what = health.Models.Count > 0 ? string.Join(", ", health.Models) : "an unknown model";
        return $"Port {port} is taken by a server OpenMono did not start (serving {what}, not {alias}). "
            + "llama-server serves only the model it was started with, so OpenMono will not reuse it. "
            + "Stop that server, then run .\\win\\openmono.ps1 start again.";
    }

    public static string Tail(string logFile, int lines = 20)
    {
        try
        {
            if (!File.Exists(logFile))
            {
                return "  (no log written)";
            }

            using var stream = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Count - lines)).Select(l => "  " + l));
        }
        catch (IOException ex)
        {
            return $"  (log unreadable: {ex.Message})";
        }
    }

    private async Task<ServerHealth> WaitReadyAsync(string endpoint, int? pid, CancellationToken ct)
    {
        ServerHealth health = new(false, false, false, [], "not checked");
        for (var i = 0; i < _healthAttempts; i++)
        {
            health = await _health.CheckAsync(endpoint, ct);
            if (health.Ready || (pid is { } p && !_host.IsRunning(p)))
            {
                return health;
            }

            await _delay(TimeSpan.FromSeconds(1), ct);
        }

        return health;
    }

    private async Task<ServerHealth> WaitPortFreeAsync(string endpoint, CancellationToken ct)
    {
        var health = await _health.CheckAsync(endpoint, ct);
        for (var i = 0; i < 10 && health.Reachable; i++)
        {
            await _delay(TimeSpan.FromSeconds(1), ct);
            health = await _health.CheckAsync(endpoint, ct);
        }

        return health;
    }
}
