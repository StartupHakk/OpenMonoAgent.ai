using OpenMono.Win.AgentHost;
using OpenMono.Win.Inference;

namespace OpenMono.Win.Launcher;

public enum CheckLevel
{
    Ok,
    Info,
    Warn,
    Fail,
}

public sealed record DoctorCheck(string Name, CheckLevel Level, string Detail, string? Fix = null);

public sealed record DoctorInput(
    string WorkDir,
    bool IsWindows,
    IReadOnlyList<string> PathDirectories,
    Func<string, bool> FileExists,
    Func<string, bool> DirectoryHasEntries,
    Func<string, IReadOnlyList<string>> ListFiles,
    int AcpPort = WindowsAcpWiring.DefaultHostPort);

// `openmono doctor` on Windows: the host prerequisites the bash doctor checks
// on Linux (docker, inference, agent image), plus the Windows-only pieces
// (PowerShell host shell, Git and the .NET SDK that setup installs, the GPU
// tier and llama.cpp build, the ladder model files, and the container-to-host
// route to llama-server). Read-only: it never starts or downloads anything.
public sealed class DoctorRunner
{
    public const string AgentImage = "openmono-agent:latest";

    private readonly LauncherDeps _deps;

    public DoctorRunner(LauncherDeps deps)
    {
        _deps = deps;
    }

    public async Task<IReadOnlyList<DoctorCheck>> RunAsync(DoctorInput input, CancellationToken ct = default)
    {
        var checks = new List<DoctorCheck>();

        if (input.IsWindows)
        {
            checks.Add(ShellCheck(input));
        }

        checks.Add(await GitCheckAsync(ct));
        checks.Add(await DotnetSdkCheckAsync(ct));

        var dockerOk = await AddDockerChecksAsync(checks, ct);
        var imagePresent = dockerOk && await ImagePresentAsync(ct);
        checks.Add(imagePresent
            ? new DoctorCheck("agent image", CheckLevel.Ok, $"{AgentImage} is built.")
            : new DoctorCheck("agent image", dockerOk ? CheckLevel.Warn : CheckLevel.Info,
                $"{AgentImage} is not built yet.", "Run: .\\win\\openmono.ps1 setup"));

        var settings = HostSettings.Load(HostSettings.PathFor(_deps.HomeDir));
        var configured = _deps.GetEnv(OmaEndpointContract.EndpointEnvVar) ?? settings.LlmEndpoint;
        var remote = configured is not null && !HostSettings.IsLocalEndpoint(configured);

        if (remote)
        {
            checks.Add(new DoctorCheck("inference", CheckLevel.Info,
                $"llm.endpoint {configured} is remote; llama.cpp checks skipped."));
        }
        else
        {
            await AddLlamaChecksAsync(checks, ct);
        }

        var state = new InferenceStateStore(_deps.HomeDir).Load();
        var endpoint = remote
            ? OmaEndpointContract.TrimRoot(configured!)
            : state?.Endpoint ?? LlamaCppConstants.DefaultLoopbackEndpoint;
        var health = await new ServerHealthChecker(_deps.Http).CheckAsync(endpoint, ct);
        checks.Add(health.Reachable
            ? new DoctorCheck("inference server", CheckLevel.Ok,
                $"{endpoint} answers; models: {(health.Models.Count > 0 ? string.Join(", ", health.Models) : "none loaded yet")}.")
            : new DoctorCheck("inference server", remote ? CheckLevel.Fail : CheckLevel.Warn,
                $"{endpoint} is not answering ({health.Error}).",
                remote ? "Check the remote server and llm.endpoint." : "Run: .\\win\\openmono.ps1 start"));

        if (imagePresent && health.Reachable && !remote)
        {
            checks.Add(await ContainerRouteCheckAsync(endpoint, ct));
        }

        checks.Add(await AcpCheckAsync(input.AcpPort, ct));

        var graph = WindowsGraphArtifacts.Check(new WindowsGraphInput(
            input.WorkDir,
            Path.Combine(_deps.HomeDir, ".openmono"),
            input.DirectoryHasEntries,
            input.FileExists));
        checks.Add(new DoctorCheck("graph", CheckLevel.Info, graph.Detail));

        return checks;
    }

    public static int ExitCode(IReadOnlyList<DoctorCheck> checks) =>
        checks.Any(c => c.Level == CheckLevel.Fail) ? 1 : 0;

    private static DoctorCheck ShellCheck(DoctorInput input)
    {
        try
        {
            var shell = WindowsShellSelector.ResolveAvailable(
                name => input.PathDirectories.Any(dir => input.FileExists(Path.Combine(dir, name))));
            return new DoctorCheck("host shell", CheckLevel.Ok, $"{shell} found.");
        }
        catch (InvalidOperationException ex)
        {
            return new DoctorCheck("host shell", CheckLevel.Fail, ex.Message, "winget install Microsoft.PowerShell");
        }
    }

    private async Task<DoctorCheck> GitCheckAsync(CancellationToken ct)
    {
        var run = await TryRunAsync("git", "--version", ct);
        var version = run is { Succeeded: true } ? HostPrerequisites.ParseGitVersion(run.StandardOutput) : null;
        return version is not null && version >= HostPrerequisites.MinGit
            ? new DoctorCheck("git", CheckLevel.Ok, $"Git {version}.")
            : new DoctorCheck("git", CheckLevel.Warn,
                version is null ? "Git not found." : $"Git {version} is older than {HostPrerequisites.MinGit}.",
                $"Run .\\win\\openmono.ps1 setup: it installs Git (winget {HostPrerequisites.GitWingetId}, or MinGit without winget).");
    }

    private async Task<DoctorCheck> DotnetSdkCheckAsync(CancellationToken ct)
    {
        var run = await TryRunAsync("dotnet", "--list-sdks", ct);
        var sdks = run is { Succeeded: true } ? HostPrerequisites.ParseSdkList(run.StandardOutput) : [];
        var usable = HostPrerequisites.UsableSdk(sdks);
        var need = $"{HostPrerequisites.MinDotnetSdk.Major}.x, {HostPrerequisites.MinDotnetSdk} or newer";
        return usable is not null
            ? new DoctorCheck("dotnet sdk", CheckLevel.Ok, $".NET SDK {usable} (needs {need}).")
            : new DoctorCheck("dotnet sdk", CheckLevel.Fail,
                sdks.Count == 0 ? $"No .NET SDK found (needs {need})." : $".NET SDK {string.Join(", ", sdks)} found; needs {need}.",
                $"Run .\\win\\openmono.ps1 setup: it installs the SDK (winget {HostPrerequisites.DotnetWingetId}, or dotnet-install.ps1 without winget).");
    }

    private async Task<bool> AddDockerChecksAsync(List<DoctorCheck> checks, CancellationToken ct)
    {
        var server = await TryRunAsync("docker", "version --format {{.Server.Version}}", ct);
        if (server is null || !server.Succeeded || string.IsNullOrWhiteSpace(server.StandardOutput))
        {
            checks.Add(new DoctorCheck("docker", CheckLevel.Fail,
                "Docker engine is not reachable.",
                "Run .\\win\\openmono.ps1 setup: it installs Docker Desktop (winget, WSL 2) if missing and starts it."));
            return false;
        }

        checks.Add(new DoctorCheck("docker", CheckLevel.Ok, $"Docker engine {server.StandardOutput.Trim()}."));

        var compose = await TryRunAsync("docker", "compose version --short", ct);
        checks.Add(compose is { Succeeded: true }
            ? new DoctorCheck("docker compose", CheckLevel.Ok, $"Compose {compose.StandardOutput.Trim()}.")
            : new DoctorCheck("docker compose", CheckLevel.Fail, "docker compose v2 is missing.",
                "Update Docker Desktop; compose v2 ships with it."));

        return compose is { Succeeded: true };
    }

    private async Task<bool> ImagePresentAsync(CancellationToken ct)
    {
        var inspect = await TryRunAsync("docker", $"image inspect {AgentImage} --format {{{{.Id}}}}", ct);
        return inspect is { Succeeded: true };
    }

    private async Task AddLlamaChecksAsync(List<DoctorCheck> checks, CancellationToken ct)
    {
        var catalog = ModelLadder.Locate(_deps.GetEnv);
        if (catalog is null)
        {
            checks.Add(new DoctorCheck("model ladder", CheckLevel.Fail, "scripts\\models.json was not found.",
                "Run OpenMono from its git checkout (git pull restores scripts\\models.json)."));
            return;
        }

        ModelLadder ladder;
        try
        {
            ladder = ModelLadder.Load(catalog);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            checks.Add(new DoctorCheck("model ladder", CheckLevel.Fail, $"{catalog}: {ex.Message}", "git checkout -- scripts/models.json"));
            return;
        }

        var orchestrator = new InferenceOrchestrator(_deps);
        var plan = await orchestrator.PlanAsync(ladder, ct);
        var backends = string.Join(", then ", plan.Accels.Select(LlamaCppRelease.Describe));
        checks.Add(plan.Gpu is { } gpu
            ? new DoctorCheck("gpu", CheckLevel.Ok,
                $"{gpu.Name}, {gpu.DedicatedVramGiB:F0} GB: serves {plan.GpuRung.Alias}; llama.cpp {backends}"
                    + (plan.DriverCuda is null ? " (the driver reports no CUDA version)." : $" (driver CUDA {plan.DriverCuda})."))
            : new DoctorCheck("gpu", CheckLevel.Info,
                $"No NVIDIA GPU detected: CPU-only, serves {plan.CpuRung.Alias} on the llama.cpp CPU build."));

        var installed = LlamaCppInstaller.ListInstalled(orchestrator.InstallRoot);
        var usable = plan.Accels
            .Select(a => LlamaCppInstaller.FindInstalled(orchestrator.InstallRoot, a, plan.DriverCuda))
            .FirstOrDefault(b => b is not null);
        checks.Add(usable is not null
            ? new DoctorCheck("llama.cpp", CheckLevel.Ok, $"llama.cpp {usable.Id} installed in {usable.Directory}.")
            : new DoctorCheck("llama.cpp", CheckLevel.Warn,
                installed.Count > 0
                    ? $"Installed llama.cpp builds ({string.Join(", ", installed.Select(b => b.Id))}) do not match this machine ({backends})."
                    : "No llama.cpp build installed yet.",
                "Run: .\\win\\openmono.ps1 setup (downloads the official llama.cpp Windows build)"));

        var settings = HostSettings.Load(HostSettings.PathFor(_deps.HomeDir));
        var rung = (settings.LlmModel is { } kept ? ladder.Find(kept) : null) ?? plan.RungFor(plan.Accels[0]);
        var modelsDir = ModelStore.ModelsDirFor(catalog, _deps.GetEnv);
        var hasModel = File.Exists(Path.Combine(modelsDir, rung.File));
        var hasMmproj = rung.Mmproj is not null && File.Exists(Path.Combine(modelsDir, rung.Mmproj));
        checks.Add(hasModel
            ? new DoctorCheck("model", CheckLevel.Ok,
                $"{rung.File} is in {modelsDir}"
                    + (hasMmproj ? " with its vision projector." : settings.VisionEnabled == false ? " (vision off in settings)." : "; the vision projector is missing, so images are off."))
            : new DoctorCheck("model", CheckLevel.Warn, $"{rung.File} ({rung.Label}) is not downloaded to {modelsDir} yet.",
                "Run: .\\win\\openmono.ps1 setup"));
    }

    // The one network hop that is new on Windows: from inside the Linux
    // container to llama-server on the Windows host loopback.
    private async Task<DoctorCheck> ContainerRouteCheckAsync(string hostEndpoint, CancellationToken ct)
    {
        var containerUrl = WindowsContainerEndpoint.MapForContainer(hostEndpoint) + OmaEndpointContract.HealthPath;
        var run = await TryRunAsync("docker",
            $"run --rm --add-host host.docker.internal:host-gateway --entrypoint curl {AgentImage} -fsS -m 5 {containerUrl}", ct);

        return run is { Succeeded: true }
            ? new DoctorCheck("container route", CheckLevel.Ok, $"The agent container reaches {containerUrl}.")
            : new DoctorCheck("container route", CheckLevel.Fail,
                $"The agent container cannot reach {containerUrl}: {(run is null ? "docker run failed" : (run.StandardError + run.StandardOutput).Trim())}",
                "Docker Desktop must forward host.docker.internal to the host loopback (WSL 2 backend). "
                    + "Check Docker Desktop networking settings and any firewall rule blocking the port.");
    }

    private async Task<DoctorCheck> AcpCheckAsync(int port, CancellationToken ct)
    {
        var url = WindowsAcpWiring.DiscoveryUrl(port);
        try
        {
            using var response = await _deps.Http.GetAsync(url, ct);
            if (response.IsSuccessStatusCode)
            {
                return new DoctorCheck("acp", CheckLevel.Ok, $"An agent answers ACP discovery at {url}.");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // No agent on the port is the normal idle state.
        }

        return new DoctorCheck("acp", CheckLevel.Info,
            $"No ACP agent on 127.0.0.1:{port}. The VS Code extension starts one, or run: openmono agent --acp-only");
    }

    private async Task<CliResult?> TryRunAsync(string file, string args, CancellationToken ct)
    {
        try
        {
            return await _deps.Cli.RunAsync(file, args, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
