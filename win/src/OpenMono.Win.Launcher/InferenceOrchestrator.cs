using OpenMono.Win.AgentHost;
using OpenMono.Win.Inference;

namespace OpenMono.Win.Launcher;

public sealed record LauncherDeps(
    ICliRunner Cli,
    HttpClient Http,
    IGpuProber Gpu,
    string HomeDir,
    Func<string, string?> GetEnv,
    IServerProcessHost Server,
    Func<TimeSpan, CancellationToken, Task>? Delay = null,
    int HealthAttempts = 180,
    TextWriter? Progress = null,
    HttpClient? DownloadHttp = null);

public sealed record UpOptions(
    string? ModelOverride = null,
    int Port = LlamaCppConstants.DefaultPort,
    bool Vision = false,
    bool Persist = true,
    bool Update = false);

public sealed record UpResult(bool Ok, InferenceState? State, IReadOnlyList<string> Log, string? Error);

public sealed record DownResult(bool Stopped, string Message);

// What this machine gets: the GPU class, the llama.cpp builds to try in
// order, and the model for each.
public sealed record MachinePlan(GpuInfo? Gpu, Version? DriverCuda, IReadOnlyList<Accel> Accels, ModelRung GpuRung, ModelRung CpuRung)
{
    public ModelRung RungFor(Accel accel) => accel == Accel.Cpu ? CpuRung : GpuRung;
}

// The Windows counterpart of the bash launcher's "make sure inference is up"
// step: on Linux that is `docker compose up -d llama-server`, on Windows it is
// the same llama-server from the official llama.cpp Windows build, running on
// the host (CUDA on NVIDIA, then Vulkan, then CPU). A remote llm.endpoint (or
// an explicit OPENMONO_ENDPOINT) is used as-is, exactly as the bash launcher does.
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

    public string InstallRoot => LlamaCppInstaller.RootFor(_deps.HomeDir, _deps.GetEnv);

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
            log.Add($"llm.endpoint {configured} is remote; using it as-is (no local llama-server).");
            var remote = new InferenceState(
                InferenceState.RemoteBackend, configured, null, null, null, null, null, null, null,
                settings.LlmModel, null, settings.VisionEnabled, false, Now());
            return new UpResult(true, remote, log, null);
        }

        var catalog = ModelLadder.Locate(_deps.GetEnv);
        if (catalog is null)
        {
            return Fail(log, "scripts\\models.json (the model ladder) was not found. Run OpenMono from its git checkout: .\\win\\openmono.ps1 start");
        }

        ModelLadder ladder;
        try
        {
            ladder = ModelLadder.Load(catalog);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            return Fail(log, $"{catalog} is not a usable model catalog: {ex.Message}");
        }

        var plan = await PlanAsync(ladder, ct);
        log.Add(plan.Gpu is { } g
            ? $"NVIDIA GPU: {g.Name}, {g.DedicatedVramGiB:F0} GB VRAM; driver CUDA {plan.DriverCuda?.ToString() ?? "unknown"}."
            : "No NVIDIA GPU detected: CPU-only.");

        var requested = options.ModelOverride;
        ModelRung? forced = null;
        if (requested is not null)
        {
            forced = ladder.Find(requested);
            if (forced is null)
            {
                return Fail(log, $"--model {requested} is not in the Windows model ladder ({catalog}). "
                    + $"Choose one of: {string.Join(", ", ladder.All.Select(r => r.Alias).Distinct())}.");
            }
        }
        else if (settings.LlmModel is { } kept)
        {
            forced = ladder.Find(kept);
            log.Add(forced is not null
                ? $"llm.model={kept} is set in settings.json; serving that ladder model."
                : $"llm.model={kept} is set in settings.json but is not a Windows ladder model; llama-server serves the ladder model and the agent keeps llm.model.");
        }

        var modelsDir = ModelStore.ModelsDirFor(catalog, _deps.GetEnv);
        var downloadHttp = _deps.DownloadHttp ?? _deps.Http;
        var store = new ModelStore(downloadHttp, modelsDir, _deps.GetEnv, _deps.Progress);
        var installer = new LlamaCppInstaller(downloadHttp, _deps.HomeDir, _deps.GetEnv, _deps.Progress);
        var service = new LlamaServerService(
            _deps.Server, new ServerHealthChecker(_deps.Http), _deps.Delay, _deps.HealthAttempts);
        var previous = _store.Load();
        var withMmproj = settings.VisionEnabled != false;
        var logFile = Path.Combine(_deps.HomeDir, ".openmono", "win", "llama-server.log");

        string? lastError = null;
        foreach (var accel in plan.Accels)
        {
            var rung = forced ?? plan.RungFor(accel);
            log.Add($"{LlamaCppRelease.Describe(accel)}: {rung.Label}.");

            var install = await installer.EnsureAsync(accel, plan.DriverCuda, options.Update, ct);
            log.AddRange(install.Log);
            if (install.Build is null)
            {
                lastError = install.Error;
                log.Add($"llama.cpp {LlamaCppRelease.Describe(accel)} build unavailable: {install.Error}");
                continue;
            }

            var files = await store.EnsureAsync(rung, withMmproj, ct);
            log.AddRange(files.Log);
            if (files.Error is not null)
            {
                return Fail(log, files.Error + " Run the same command again to resume the download.");
            }

            var context = files.MmprojPath is null ? rung.Context : rung.VisionContext;
            var config = new LlamaServerConfig(
                files.ModelPath,
                files.MmprojPath,
                rung.Alias,
                options.Port,
                context,
                rung.Kv,
                accel,
                LlamaServerArgs.DefaultThreads(_deps.GetEnv, accel),
                logFile,
                _deps.GetEnv(LlamaCppConstants.ApiKeyEnvVar));

            var running = previous is { Backend: InferenceState.LlamaBackend, StartedByUs: true, ServerFingerprint: { } fp }
                ? new RunningServer(previous.ServerPid, fp)
                : null;
            var served = await service.StartOrAdoptAsync(install.Build, config, running, InstallRoot, ct);
            log.AddRange(served.Log);
            if (!served.Ok)
            {
                lastError = served.Error;
                if (served.Crashed && accel != plan.Accels[^1])
                {
                    log.Add($"WARNING: the llama.cpp {LlamaCppRelease.Describe(accel)} build failed ({served.Error}); "
                        + "falling back to the next backend.");
                    continue;
                }

                return Fail(log, served.Error!);
            }

            var vision = WindowsVisionConfig.Resolve(settings.VisionEnabled, files.MmprojPath is not null, rung.Alias);
            log.Add(vision.Reason);
            if (options.Vision && !vision.VisionEnabled)
            {
                return Fail(log, $"{vision.Reason} A vision session cannot start; the images were not sent.");
            }

            if (options.Vision)
            {
                log.Add($"Vision session: {rung.Alias} takes images; they are sent with each turn.");
            }

            var agentModel = settings.LlmModel is not null && forced is null && requested is null ? null : rung.Alias;
            var state = new InferenceState(
                InferenceState.LlamaBackend,
                $"http://127.0.0.1:{options.Port}",
                rung.Alias,
                files.ModelPath,
                accel.ToString().ToLowerInvariant(),
                install.Build.Id,
                served.Pid,
                LlamaServerArgs.Fingerprint(install.Build.Id, config),
                logFile,
                agentModel,
                context,
                vision.VisionEnabled,
                served.StartedByUs,
                Now());

            if (options.Persist)
            {
                _store.Save(state);
            }

            return new UpResult(true, state, log, null);
        }

        return Fail(log, lastError ?? "No llama.cpp backend could start on this machine.");
    }

    // Which builds to try and which model each serves. NVIDIA: CUDA when the
    // driver reports a CUDA version, else Vulkan; CPU is always last. Without
    // NVIDIA there is no GPU rung: the CPU model only.
    public async Task<MachinePlan> PlanAsync(ModelLadder ladder, CancellationToken ct = default)
    {
        var gpus = await _deps.Gpu.ProbeAsync(ct);
        var nvidia = gpus.Where(g => g.IsNvidia).OrderByDescending(g => g.DedicatedVramBytes).FirstOrDefault();
        if (nvidia is null)
        {
            return new MachinePlan(null, null, [Accel.Cpu], ladder.Cpu, ladder.Cpu);
        }

        Version? cuda = null;
        try
        {
            var smi = await _deps.Cli.RunAsync("nvidia-smi", "", ct);
            cuda = smi.Succeeded ? LlamaCppRelease.ParseDriverCudaVersion(smi.StandardOutput) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No banner: the CUDA build cannot be matched to the driver.
        }

        Accel[] accels = cuda is null ? [Accel.Vulkan, Accel.Cpu] : [Accel.Cuda, Accel.Vulkan, Accel.Cpu];
        return new MachinePlan(nvidia, cuda, accels, ladder.Pick(nvidia.DedicatedVramBytes), ladder.Cpu);
    }

    public Task<DownResult> DownAsync(bool force) => Task.FromResult(Down(force));

    private DownResult Down(bool force)
    {
        var state = _store.Load();
        if (state?.Backend == InferenceState.RemoteBackend)
        {
            _store.Delete();
            return new DownResult(false, "Inference is remote; nothing local to stop.");
        }

        if (state is { StartedByUs: false } && !force)
        {
            return new DownResult(false,
                $"The server on {state.Endpoint} was not started by OpenMono; leaving it running. Use --force to stop OpenMono's llama-server anyway.");
        }

        var pids = _deps.Server.FindUnder(InstallRoot).ToList();
        if (state?.ServerPid is { } pid && _deps.Server.IsRunning(pid) && !pids.Contains(pid))
        {
            pids.Add(pid);
        }

        foreach (var p in pids)
        {
            _deps.Server.Stop(p);
        }

        _store.Delete();
        return pids.Count > 0
            ? new DownResult(true, $"llama-server stopped (pid {string.Join(", ", pids)}).")
            : new DownResult(false, "No llama-server started by OpenMono is running.");
    }

    private static UpResult Fail(List<string> log, string error) => new(false, null, log, error);

    private static string Now() => DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
}
