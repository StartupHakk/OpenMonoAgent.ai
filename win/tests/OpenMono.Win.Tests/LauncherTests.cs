using System.Net;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Win.Foundry;
using OpenMono.Win.Launcher;
using Xunit;

namespace OpenMono.Win.Tests;

public sealed class FakeGpuProber : IGpuProber
{
    private readonly IReadOnlyList<GpuInfo> _gpus;

    public FakeGpuProber(params GpuInfo[] gpus)
    {
        _gpus = gpus;
    }

    public Task<IReadOnlyList<GpuInfo>> ProbeAsync(CancellationToken ct = default) => Task.FromResult(_gpus);
}

// A scripted Foundry: CLI answers plus an HTTP server that comes up on
// `server start` and lists a model once `model load` ran for it.
public sealed class FoundryRig : IDisposable
{
    private const long GiB = 1024L * 1024 * 1024;

    private readonly HashSet<string> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private bool _serving;

    public static readonly string[] LadderCatalog =
        ["qwen3.8-27b", "qwen3.5-9b", "qwen3.5-4b", "qwen3.5-2b", "qwen3.5-0.8b", "qwen3-8b"];

    public FoundryRig(long vramGiB = 24, bool imagesAccepted = false, bool alreadyServing = false, string[]? catalog = null)
    {
        Home = Path.Combine(Path.GetTempPath(), "omw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Home, ".openmono"));
        _serving = alreadyServing;

        Runner.Respond(Exe, "--version", 0, "foundry 0.13.0\n");
        var aliases = catalog ?? LadderCatalog;
        Runner.Respond(Exe, "model list --output json", 0,
            JsonSerializer.Serialize(aliases.Select(a => new { alias = a })));
        Runner.Respond(Exe, "server status", 1, "", "not running");
        Runner.Respond(Exe, "server start --port 7474 --idle-timeout 0", 0, "started\n");
        Runner.OnCall(Exe, "server start --port 7474 --idle-timeout 0", () => _serving = true);
        Runner.Respond(Exe, "server stop", 0, "stopped\n");
        foreach (var alias in LadderCatalog)
        {
            Runner.Respond(Exe, $"model load {alias}", 0, "loaded\n");
            Runner.OnCall(Exe, $"model load {alias}", () => _loaded.Add(alias));
        }

        Http.RespondJson("/v1/models", () => JsonSerializer.Serialize(new
        {
            data = _loaded.Select(a => new { id = a + "-cuda-gpu:1" }),
        }));
        Http.Respond("/v1/chat/completions", imagesAccepted
            ? FakeHttpHandler.Json("""{"choices":[{"message":{"content":"ok"}}]}""")
            : FakeHttpHandler.Json("""{"error":"image input not supported"}""", HttpStatusCode.BadRequest));

        Gpu = new FakeGpuProber(new GpuInfo("NVIDIA GeForce RTX 4090", vramGiB * GiB, true));
        Client = new HttpClient(new GatedHandler(Http, () => _serving)) { Timeout = TimeSpan.FromSeconds(5) };
    }

    public static string Exe => FoundryCliDetector.CliExecutableName;

    public string Home { get; }

    public FakeCliRunner Runner { get; } = new();

    public FakeHttpHandler Http { get; } = new();

    public FakeGpuProber Gpu { get; set; }

    public HttpClient Client { get; }

    public Dictionary<string, string> Env { get; } = new();

    public void Preload(string alias) => _loaded.Add(alias);

    public void WriteSettings(string json) =>
        File.WriteAllText(Path.Combine(Home, ".openmono", "settings.json"), json);

    public LauncherDeps Deps() => new(
        Runner, Client, Gpu, Home, key => Env.GetValueOrDefault(key), (_, _) => Task.CompletedTask, 3);

    public void Dispose()
    {
        Client.Dispose();
        try
        {
            Directory.Delete(Home, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best effort.
        }
    }
}

public sealed class InferenceOrchestratorTests
{
    [Fact]
    public async Task ServesTargetModelOn24GbAndPersistsState()
    {
        using var rig = new FoundryRig(vramGiB: 24);
        var orchestrator = new InferenceOrchestrator(rig.Deps());

        var result = await orchestrator.UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        var state = result.State!;
        state.Backend.Should().Be(InferenceState.FoundryBackend);
        state.Endpoint.Should().Be("http://127.0.0.1:7474");
        state.ModelAlias.Should().Be("qwen3.8-27b");
        state.ServedModelId.Should().Be("qwen3.8-27b-cuda-gpu:1");
        state.AgentModel.Should().Be("qwen3.8-27b-cuda-gpu:1");
        state.ContextTokens.Should().Be(65536);
        state.StartedByUs.Should().BeTrue();
        orchestrator.Store.Load().Should().BeEquivalentTo(state);
    }

    [Fact]
    public async Task ThirtyTwoGbGetsFullContext()
    {
        using var rig = new FoundryRig(vramGiB: 32);

        var result = await new InferenceOrchestrator(rig.Deps()).UpAsync(new UpOptions());

        result.State!.ContextTokens.Should().Be(262144);
    }

    [Theory]
    [InlineData(12, "qwen3.5-9b")]
    [InlineData(8, "qwen3.5-4b")]
    [InlineData(4, "qwen3.5-2b")]
    [InlineData(2, "qwen3.5-0.8b")]
    public async Task SmallerCardsServeTheQwen35Ladder(long vramGiB, string alias)
    {
        using var rig = new FoundryRig(vramGiB: vramGiB, imagesAccepted: true);

        var result = await new InferenceOrchestrator(rig.Deps()).UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        result.State!.ModelAlias.Should().Be(alias);
        result.State.ServedModelId.Should().Be(alias + "-cuda-gpu:1");
        result.State.VisionEnabled.Should().BeTrue();
        result.Log.Should().Contain(l => l.Contains("below the 24 GB floor"));
        rig.Runner.Calls.Where(c => c.Arguments.StartsWith("model load", StringComparison.Ordinal))
            .Select(c => c.Arguments).Should().Equal($"model load {alias}");
    }

    [Fact]
    public async Task MissingRungStepsDownToTheNextQwen35Model()
    {
        using var rig = new FoundryRig(vramGiB: 12, catalog: ["qwen3.8-27b", "qwen3.5-4b", "qwen3.5-2b"]);

        var result = await new InferenceOrchestrator(rig.Deps()).UpAsync(new UpOptions());

        result.State!.ModelAlias.Should().Be("qwen3.5-4b");
        result.Log.Should().Contain(l => l.Contains("Not in the Foundry catalog: qwen3.5-9b"));
    }

    [Fact]
    public async Task TwentyFourGbWithoutTheTargetInTheCatalogFailsAndDownloadsNothing()
    {
        using var rig = new FoundryRig(vramGiB: 24, catalog: ["qwen3.5-9b", "qwen3.5-4b"]);

        var result = await new InferenceOrchestrator(rig.Deps()).UpAsync(new UpOptions());

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("serves only qwen3.8-27b").And.Contain("nothing was downloaded");
        rig.Runner.Calls.Should().NotContain(c => c.Arguments.StartsWith("model load", StringComparison.Ordinal));
        rig.Runner.Calls.Should().NotContain(c => c.Arguments.StartsWith("server start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ModelOverrideOutsideTheCatalogIsRefused()
    {
        using var rig = new FoundryRig();

        var result = await new InferenceOrchestrator(rig.Deps()).UpAsync(new UpOptions(ModelOverride: "qwen2.5-vl-7b"));

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("not in the Foundry catalog").And.Contain("https://foundrylocal.ai/models");
        rig.Runner.Calls.Should().NotContain(c => c.Arguments.StartsWith("model load", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmptyLocalCatalogIsValidatedAgainstThePublicCatalog()
    {
        using var rig = new FoundryRig(vramGiB: 12, alreadyServing: true, catalog: []);
        rig.Runner.Respond(FoundryRig.Exe, "model list", 0, "");
        rig.Http.RespondJson("/api/models", () => CatalogFixtures.Page2Only);

        var result = await new InferenceOrchestrator(rig.Deps()).UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        result.State!.ModelAlias.Should().Be("qwen3.5-4b");
        result.Log.Should().Contain(l => l.Contains("Not in the Foundry catalog: qwen3.5-9b"));
        result.Log.Should().Contain(l => l.StartsWith("qwen3.5-4b: about ", StringComparison.Ordinal) && l.Contains("GB download"));
    }

    [Fact]
    public async Task TextOnlyModelTurnsVisionOffAndSaysHowToGetImages()
    {
        using var rig = new FoundryRig(imagesAccepted: false);

        var result = await new InferenceOrchestrator(rig.Deps()).UpAsync(new UpOptions());

        result.State!.VisionEnabled.Should().BeFalse();
        result.State.ModelAlias.Should().Be("qwen3.8-27b");
        result.Log.Should().Contain(l => l.StartsWith("Images:") && l.Contains("does not load a second model"));
    }

    [Fact]
    public async Task ImageCapableModelTurnsVisionOn()
    {
        using var rig = new FoundryRig(imagesAccepted: true);

        var result = await new InferenceOrchestrator(rig.Deps()).UpAsync(new UpOptions());

        result.State!.VisionEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task ExplicitVisionSettingWinsOverProbe()
    {
        using var rig = new FoundryRig(imagesAccepted: true);
        rig.WriteSettings("""{"inference":{"vision_enabled":false}}""");

        var result = await new InferenceOrchestrator(rig.Deps()).UpAsync(new UpOptions());

        result.State!.VisionEnabled.Should().BeFalse();
    }

    [Theory]
    [InlineData(24, "qwen3.8-27b")]
    [InlineData(12, "qwen3.5-9b")]
    public async Task VisionSessionServesTheSameModel(long vramGiB, string alias)
    {
        using var rig = new FoundryRig(vramGiB: vramGiB, imagesAccepted: true);
        var orchestrator = new InferenceOrchestrator(rig.Deps());

        var result = await orchestrator.UpAsync(new UpOptions(Vision: true));

        result.Ok.Should().BeTrue(result.Error);
        result.State!.ModelAlias.Should().Be(alias);
        result.State.AgentModel.Should().Be(alias + "-cuda-gpu:1");
        result.State.VisionEnabled.Should().BeTrue();
        result.Log.Should().Contain(l => l.StartsWith("Vision session:"));
    }

    [Fact]
    public async Task TwentyFourGbVisionSessionNeverLoadsASecondModel()
    {
        using var rig = new FoundryRig(vramGiB: 24, imagesAccepted: false);

        var result = await new InferenceOrchestrator(rig.Deps()).UpAsync(new UpOptions(Vision: true));

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("images were not sent").And.Contain("does not load a second one");
        rig.Runner.Calls.Where(c => c.Arguments.StartsWith("model load", StringComparison.Ordinal))
            .Select(c => c.Arguments).Should().Equal("model load qwen3.8-27b");
    }

    [Fact]
    public async Task ExplicitLlmModelIsKept()
    {
        using var rig = new FoundryRig();
        rig.WriteSettings("""{"llm":{"endpoint":"http://127.0.0.1:7474","model":"my-model"}}""");

        var result = await new InferenceOrchestrator(rig.Deps()).UpAsync(new UpOptions());

        result.State!.AgentModel.Should().BeNull();
    }

    [Fact]
    public async Task RemoteEndpointIsUsedAsIs()
    {
        using var rig = new FoundryRig();
        rig.WriteSettings("""{"llm":{"endpoint":"http://gpu-box.lan:7474","model":"qwen"}}""");

        var result = await new InferenceOrchestrator(rig.Deps()).UpAsync(new UpOptions());

        result.Ok.Should().BeTrue();
        result.State!.Backend.Should().Be(InferenceState.RemoteBackend);
        result.State.Endpoint.Should().Be("http://gpu-box.lan:7474");
        rig.Runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task EnvironmentEndpointWinsOverSettings()
    {
        using var rig = new FoundryRig();
        rig.WriteSettings("""{"llm":{"endpoint":"http://127.0.0.1:7474"}}""");
        rig.Env["OPENMONO_ENDPOINT"] = "http://gpu-box.lan:9000";

        var result = await new InferenceOrchestrator(rig.Deps()).UpAsync(new UpOptions());

        result.State!.Endpoint.Should().Be("http://gpu-box.lan:9000");
    }

    [Fact]
    public async Task MissingCliPointsAtSetup()
    {
        using var rig = new FoundryRig();
        var deps = rig.Deps() with { Cli = new FakeCliRunner() };

        var result = await new InferenceOrchestrator(deps).UpAsync(new UpOptions());

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("winget install Microsoft.FoundryLocal");
    }

    [Fact]
    public async Task OldCliIsRejected()
    {
        using var rig = new FoundryRig();
        rig.Runner.Respond(FoundryRig.Exe, "--version", 0, "foundry 0.9.1\n");

        var result = await new InferenceOrchestrator(rig.Deps()).UpAsync(new UpOptions());

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("winget upgrade Microsoft.FoundryLocal");
    }

    [Fact]
    public async Task CorruptSettingsStopEarly()
    {
        using var rig = new FoundryRig();
        rig.WriteSettings("{ \"llm\": { } } trailing");

        var result = await new InferenceOrchestrator(rig.Deps()).UpAsync(new UpOptions());

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("settings.json is corrupt");
    }

    [Fact]
    public async Task AdoptedDaemonIsNotStoppedWithoutForce()
    {
        using var rig = new FoundryRig(alreadyServing: true);
        rig.Preload("qwen3.8-27b");
        var orchestrator = new InferenceOrchestrator(rig.Deps());
        (await orchestrator.UpAsync(new UpOptions())).State!.StartedByUs.Should().BeFalse();

        var down = await orchestrator.DownAsync(force: false);

        down.Stopped.Should().BeFalse();
        down.Message.Should().Contain("--force");
        rig.Runner.Calls.Should().NotContain(c => c.Arguments == "server stop");
    }

    [Fact]
    public async Task DaemonWeStartedIsStopped()
    {
        using var rig = new FoundryRig();
        var orchestrator = new InferenceOrchestrator(rig.Deps());
        await orchestrator.UpAsync(new UpOptions());

        var down = await orchestrator.DownAsync(force: false);

        down.Stopped.Should().BeTrue();
        rig.Runner.Calls.Should().Contain(c => c.Arguments == "server stop");
        orchestrator.Store.Load().Should().BeNull();
    }

    [Fact]
    public async Task RepeatLaunchReusesVisionProbe()
    {
        using var rig = new FoundryRig();
        var orchestrator = new InferenceOrchestrator(rig.Deps());
        await orchestrator.UpAsync(new UpOptions());

        var second = await orchestrator.UpAsync(new UpOptions());

        second.Log.Should().Contain(l => l.StartsWith("Reusing the earlier image probe"));
        second.State!.StartedByUs.Should().BeTrue();
    }
}

public sealed class LauncherCliTests
{
    [Fact]
    public void ArgsSplitOwnFlagsFromAgentPassthrough()
    {
        var parsed = LauncherArgs.Parse(
            ["agent-plan", "--workdir", "C:\\proj", "--vision", "--", "--acp-only", "--model", "x"]);

        parsed.Command.Should().Be("agent-plan");
        parsed.Value("--workdir").Should().Be("C:\\proj");
        parsed.Flag("--vision").Should().BeTrue();
        parsed.Value("--model").Should().BeNull();
        parsed.Passthrough.Should().Equal("--acp-only", "--model", "x");
    }

    [Fact]
    public void TwoWordInferenceCommands()
    {
        LauncherArgs.Parse(["inference", "up", "--json"]).Command.Should().Be("inference up");
        LauncherArgs.Parse(["catalog", "check", "--strict"]).Command.Should().Be("catalog check");
        LauncherArgs.Parse(["catalog", "--device", "cpu"]).Command.Should().Be("catalog");
        LauncherArgs.Parse([]).Command.Should().Be("help");
    }

    [Fact]
    public async Task AgentPlanEmitsComposeEnvironmentAndArgs()
    {
        using var rig = new FoundryRig();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await LauncherCli.RunAsync(
            ["agent-plan", "--workdir", "C:\\proj", "--compose", "C:\\oma\\docker\\docker-compose.yml", "--no-git-auth", "--", "--acp-only"],
            rig.Deps(), stdout, stderr);

        code.Should().Be(0, stderr.ToString());
        using var doc = JsonDocument.Parse(stdout.ToString());
        var root = doc.RootElement;
        root.GetProperty("environment").GetProperty("OPENMONO_ENDPOINT").GetString()
            .Should().Be("http://host.docker.internal:7474");
        root.GetProperty("environment").GetProperty("CTX_SIZE").GetString().Should().Be("65536");
        root.GetProperty("acp_port").GetInt32().Should().Be(7475);
        var args = root.GetProperty("docker_args").EnumerateArray().Select(e => e.GetString()).ToList();
        args.Should().Contain("127.0.0.1:7475:7475");
        args.Should().Contain("OPENMONO_MODEL=qwen3.8-27b-cuda-gpu:1");
        args.TakeLast(2).Should().Equal("agent", "--acp-only");
        root.GetProperty("notes").EnumerateArray().Select(e => e.GetString())
            .Should().Contain(n => n!.StartsWith("Images:"));
    }

    [Fact]
    public async Task AgentArgsCanArriveAsJsonEnv()
    {
        using var rig = new FoundryRig();
        rig.Env[LauncherCli.AgentArgsEnvVar] = """["-p","say \"hi\" to C:\\proj"]""";
        var stdout = new StringWriter();

        var code = await LauncherCli.RunAsync(
            ["agent-plan", "--workdir", "C:\\proj", "--compose", "c.yml"], rig.Deps(), stdout, new StringWriter());

        code.Should().Be(0);
        using var doc = JsonDocument.Parse(stdout.ToString());
        doc.RootElement.GetProperty("docker_args").EnumerateArray().Select(e => e.GetString()).TakeLast(3)
            .Should().Equal("agent", "-p", "say \"hi\" to C:\\proj");
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("not json", 0)]
    [InlineData("\"--acp-only\"", 1)]
    [InlineData("[\"--acp-only\",\"--acp-port\",\"7575\"]", 3)]
    public void AgentArgsEnvParsing(string? json, int count)
    {
        LauncherCli.AgentArgsFromEnv(json).Should().HaveCount(count);
    }

    [Fact]
    public async Task AgentPlanFailureGoesToStderr()
    {
        using var rig = new FoundryRig();
        var deps = rig.Deps() with { Cli = new FakeCliRunner() };
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await LauncherCli.RunAsync(
            ["agent-plan", "--workdir", "C:\\proj", "--compose", "c.yml"], deps, stdout, stderr);

        code.Should().Be(1);
        stdout.ToString().Should().BeEmpty();
        stderr.ToString().Should().Contain("Foundry Local CLI not found");
    }

    [Fact]
    public async Task UnknownCommandIsUsageError()
    {
        using var rig = new FoundryRig();

        var code = await LauncherCli.RunAsync(["frobnicate"], rig.Deps(), new StringWriter(), new StringWriter());

        code.Should().Be(2);
    }
}

public sealed class DoctorRunnerTests
{
    private static DoctorInput Input(bool isWindows = false) => new(
        WorkDir: "C:\\proj",
        IsWindows: isWindows,
        PathDirectories: ["C:\\Windows\\System32\\WindowsPowerShell\\v1.0"],
        FileExists: p => p.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase),
        DirectoryHasEntries: _ => false,
        ListFiles: _ => []);

    [Fact]
    public async Task MissingDockerFails()
    {
        using var rig = new FoundryRig();

        var checks = await new DoctorRunner(rig.Deps()).RunAsync(Input());

        checks.Should().Contain(c => c.Name == "docker" && c.Level == CheckLevel.Fail);
        DoctorRunner.ExitCode(checks).Should().Be(1);
    }

    [Fact]
    public async Task HealthyMachinePasses()
    {
        using var rig = new FoundryRig(alreadyServing: true);
        rig.Preload("qwen3.8-27b");
        rig.Runner.Respond("docker", "version --format {{.Server.Version}}", 0, "28.1.1\n");
        rig.Runner.Respond("docker", "compose version --short", 0, "2.35.1\n");
        rig.Runner.Respond("docker", "image inspect openmono-agent:latest --format {{.Id}}", 0, "sha256:abc\n");
        rig.Runner.Respond("docker",
            "run --rm --add-host host.docker.internal:host-gateway --entrypoint curl openmono-agent:latest -fsS -m 5 http://host.docker.internal:7474/v1/models",
            0, "{\"data\":[]}");

        var checks = await new DoctorRunner(rig.Deps()).RunAsync(Input(isWindows: true) with
        {
            ListFiles = _ => ["cublas64_12.dll", "cudnn64_9.dll", "nvcuda.dll"],
        });

        checks.Where(c => c.Level == CheckLevel.Fail).Should().BeEmpty();
        checks.Should().Contain(c => c.Name == "host shell" && c.Detail.Contains("powershell.exe"));
        checks.Should().Contain(c => c.Name == "container route" && c.Level == CheckLevel.Ok);
        checks.Should().Contain(c => c.Name == "acp" && c.Level == CheckLevel.Info);
        checks.Should().Contain(c => c.Name == "model catalog" && c.Level == CheckLevel.Ok && c.Detail.StartsWith("qwen3.8-27b is in"));
        DoctorRunner.ExitCode(checks).Should().Be(0);
    }

    [Fact]
    public async Task TwentyFourGbWithoutTheTargetModelFailsTheCatalogCheck()
    {
        using var rig = new FoundryRig(catalog: ["qwen3.5-9b", "qwen3.5-4b"]);

        var checks = await new DoctorRunner(rig.Deps()).RunAsync(Input());

        checks.Should().Contain(c => c.Name == "model catalog" && c.Level == CheckLevel.Fail
            && c.Detail.Contains("serves only qwen3.8-27b"));
    }

    [Fact]
    public async Task SmallerCardReportsItsQwen35Rung()
    {
        using var rig = new FoundryRig(vramGiB: 8);

        var checks = await new DoctorRunner(rig.Deps()).RunAsync(Input());

        checks.Should().Contain(c => c.Name == "model catalog" && c.Level == CheckLevel.Ok && c.Detail.StartsWith("qwen3.5-4b is in"));
        checks.Should().Contain(c => c.Detail.Contains("below the 24 GB floor for qwen3.8-27b"));
    }

    [Fact]
    public async Task BlockedContainerRouteFails()
    {
        using var rig = new FoundryRig(alreadyServing: true);
        rig.Runner.Respond("docker", "version --format {{.Server.Version}}", 0, "28.1.1\n");
        rig.Runner.Respond("docker", "compose version --short", 0, "2.35.1\n");
        rig.Runner.Respond("docker", "image inspect openmono-agent:latest --format {{.Id}}", 0, "sha256:abc\n");
        rig.Runner.Respond("docker",
            "run --rm --add-host host.docker.internal:host-gateway --entrypoint curl openmono-agent:latest -fsS -m 5 http://host.docker.internal:7474/v1/models",
            7, "", "curl: (7) Failed to connect");

        var checks = await new DoctorRunner(rig.Deps()).RunAsync(Input());

        checks.Should().Contain(c => c.Name == "container route" && c.Level == CheckLevel.Fail);
    }

    [Fact]
    public async Task RemoteEndpointSkipsFoundryChecks()
    {
        using var rig = new FoundryRig();
        rig.WriteSettings("""{"llm":{"endpoint":"http://gpu-box.lan:7474"}}""");

        var checks = await new DoctorRunner(rig.Deps()).RunAsync(Input());

        checks.Should().NotContain(c => c.Name == "foundry cli");
        checks.Should().Contain(c => c.Name == "inference" && c.Level == CheckLevel.Info);
    }
}

public sealed class HostSettingsTests
{
    [Fact]
    public void ReadsTheKeysTheLauncherUses()
    {
        var settings = HostSettings.Parse(
            """{"llm":{"endpoint":"http://127.0.0.1:7474","model":"m"},"inference":{"vision_enabled":true},"web":{"gateway":"http://g:8080","search":"1"}}""");

        settings.LlmEndpoint.Should().Be("http://127.0.0.1:7474");
        settings.LlmModel.Should().Be("m");
        settings.VisionEnabled.Should().BeTrue();
        settings.WebGateway.Should().Be("http://g:8080");
        settings.WebSearch.Should().Be("1");
        settings.Corrupt.Should().BeFalse();
    }

    [Theory]
    [InlineData("http://127.0.0.1:7474", true)]
    [InlineData("http://localhost:7474/v1", true)]
    [InlineData("http://host.docker.internal:7474", true)]
    [InlineData("http://gpu-box.lan:7474", false)]
    public void LocalEndpointMatchesBashLauncher(string endpoint, bool local)
    {
        HostSettings.IsLocalEndpoint(endpoint).Should().Be(local);
    }

    [Fact]
    public void MissingFileIsEmpty()
    {
        HostSettings.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "settings.json"))
            .Should().Be(HostSettings.Empty);
    }
}
