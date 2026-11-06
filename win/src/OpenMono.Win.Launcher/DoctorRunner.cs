using OpenMono.Win.AgentHost;
using OpenMono.Win.Foundry;

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
// (PowerShell host shell, Foundry CLI version, CUDA/cuDNN on PATH, and the
// container-to-host route to Foundry). Read-only: it never starts anything.
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
                $"llm.endpoint {configured} is remote; Foundry Local checks skipped."));
        }
        else
        {
            await AddFoundryChecksAsync(checks, input, ct);
        }

        var state = new InferenceStateStore(_deps.HomeDir).Load();
        var endpoint = remote
            ? OmaEndpointContract.TrimRoot(configured!)
            : state?.Endpoint ?? FoundryConstants.DefaultLoopbackEndpoint;
        var health = await new FoundryHealthChecker(_deps.Http).CheckAsync(endpoint, ct);
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

    private async Task AddFoundryChecksAsync(List<DoctorCheck> checks, DoctorInput input, CancellationToken ct)
    {
        var cli = await new FoundryCliDetector(_deps.Cli).DetectAsync(ct);
        checks.Add(!cli.Found
            ? new DoctorCheck("foundry cli", CheckLevel.Fail, "Foundry Local CLI not found.",
                "winget install Microsoft.FoundryLocal (or run .\\win\\openmono.ps1 setup)")
            : cli.IsSupported
                ? new DoctorCheck("foundry cli", CheckLevel.Ok, $"Foundry Local CLI {cli.Version}.")
                : new DoctorCheck("foundry cli", CheckLevel.Fail,
                    $"Foundry Local CLI {cli.Version?.ToString() ?? "(unknown)"} is older than {FoundryConstants.MinCliVersion}.",
                    "winget upgrade Microsoft.FoundryLocal"));

        var gpus = await _deps.Gpu.ProbeAsync(ct);
        var nvidia = gpus.Where(g => g.IsNvidia).OrderByDescending(g => g.DedicatedVramBytes).FirstOrDefault();
        if (nvidia is null)
        {
            checks.Add(new DoctorCheck("gpu", CheckLevel.Warn,
                $"No NVIDIA GPU detected. {FoundryConstants.TargetModelAlias} needs 24 GB VRAM; "
                    + "smaller NVIDIA cards get the Qwen3.5 ladder (see `openmono catalog check`)."));
        }
        else
        {
            var rung = ModelSelector.RungFor(nvidia.DedicatedVramBytes);
            var isTarget = ReferenceEquals(rung, FoundryModelLadder.Target);
            checks.Add(new DoctorCheck("gpu",
                isTarget ? CheckLevel.Ok : CheckLevel.Info,
                $"{nvidia.Name}, {nvidia.DedicatedVramGiB:F0} GB: {rung.VramClass} class, serves {rung.Alias}"
                    + (isTarget ? "." : $" (below the 24 GB floor for {FoundryConstants.TargetModelAlias}; text and images).")));
        }

        checks.Add(await ModelCatalogCheckAsync(ct));

        if (input.IsWindows)
        {
            var cuda = CudaPrerequisiteChecker.Check(
                new CudaCheckInput(input.PathDirectories, input.ListFiles),
                smiFound: nvidia is not null);
            checks.Add(cuda.Ok
                ? new DoctorCheck("cuda", CheckLevel.Ok, "CUDA and cuDNN runtimes are on PATH.")
                : new DoctorCheck("cuda", nvidia is null ? CheckLevel.Info : CheckLevel.Fail,
                    string.Join(" ", cuda.Issues), cuda.Remediation));
        }
    }

    private async Task<DoctorCheck> ModelCatalogCheckAsync(CancellationToken ct)
    {
        var check = await CatalogCheck.RunAsync(_deps, ct: ct);
        var web = check.View.Web!;
        if (check.Unusable)
        {
            return new DoctorCheck("model catalog", CheckLevel.Fail, check.Error!,
                $"Update Foundry Local (winget upgrade Microsoft.FoundryLocal); see {FoundryWebCatalogClient.ModelsPageUrl}");
        }

        if (!check.View.Known)
        {
            return new DoctorCheck("model catalog", CheckLevel.Warn,
                CatalogCheck.PublicSummary(web) + " `foundry model load` will validate the alias.",
                "Check the network, then: .\\win\\openmono.ps1 catalog check --refresh");
        }

        var pick = check.Pick!.ModelAlias;
        var missing = check.MissingFromPublic;
        return missing.Count == 0
            ? new DoctorCheck("model catalog", CheckLevel.Ok,
                $"{pick} is in the Foundry catalog ({check.View.Source}). {CatalogCheck.PublicSummary(web)}")
            : new DoctorCheck("model catalog", CheckLevel.Warn,
                $"{pick} is in the Foundry catalog ({check.View.Source}), but the public catalog lacks: {string.Join(", ", missing)}.",
                "Run: .\\win\\openmono.ps1 catalog check --refresh");
    }

    // The one network hop that is new on Windows: from inside the Linux
    // container to Foundry on the Windows host loopback.
    private async Task<DoctorCheck> ContainerRouteCheckAsync(string hostEndpoint, CancellationToken ct)
    {
        var containerUrl = WindowsContainerEndpoint.MapForContainer(hostEndpoint) + OmaEndpointContract.ModelsPath;
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
