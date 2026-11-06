using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Win.Inference;
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

// A scripted Windows host for llama.cpp: a GPU of the given size, the real
// scripts/models.json in a temp checkout, a GitHub release feed with the real
// Windows asset names, and a fake llama-server that serves whatever the fake
// process host last started.
public sealed class LlamaRig : IDisposable
{
    public const long GiB = 1024L * 1024 * 1024;

    public const string Tag = "b9000";

    public const string Top = "Qwen3.8-27B-UD-Q4_K_M";

    public const string Cpu = "Qwen3.5-2B-UD-Q4_K_XL";

    public LlamaRig(long vramGiB = 24, bool noGpu = false, string driverCuda = "12.8")
    {
        Home = Path.Combine(Path.GetTempPath(), "omw-" + Guid.NewGuid().ToString("N"));
        Repo = Path.Combine(Home, "repo");
        Directory.CreateDirectory(Path.Combine(Home, ".openmono"));
        Directory.CreateDirectory(Path.Combine(Repo, "scripts"));
        File.Copy(Path.Combine(RepoPaths.RepoRoot, "scripts", "models.json"), Path.Combine(Repo, "scripts", "models.json"));
        Env[LlamaCppConstants.RepoDirEnvVar] = Repo;
        Env[LlamaCppConstants.ReleasesUrlEnvVar] = "http://api.test/releases";

        Gpu = noGpu
            ? new FakeGpuProber(new GpuInfo("Microsoft Basic Display Adapter", 0, false))
            : new FakeGpuProber(new GpuInfo("NVIDIA GeForce RTX 4090", vramGiB * GiB, true));
        Runner.Respond("nvidia-smi", "", 0,
            $"| NVIDIA-SMI 572.16    Driver Version: 572.16    CUDA Version: {driverCuda}     |\n");

        Llama = new FakeLlamaServer(Http);
        Host = new FakeServerHost(Llama);
        Http.RespondJson("api.test/releases", () => ReleasesJson(Tag));
        foreach (var asset in AssetNames(Tag))
        {
            Http.RespondBytes("dl.test/" + asset, () => Zip(asset));
        }

        Client = new HttpClient(Http) { Timeout = TimeSpan.FromSeconds(5) };
        PlaceAllModels();
    }

    public string Home { get; }

    public string Repo { get; }

    public string ModelsDir => Path.Combine(Repo, "models");

    public string InstallRoot => Path.Combine(Home, ".openmono", "win", "llama.cpp");

    public FakeCliRunner Runner { get; } = new();

    public FakeHttpHandler Http { get; } = new();

    public FakeLlamaServer Llama { get; }

    public FakeServerHost Host { get; }

    public FakeGpuProber Gpu { get; set; }

    public HttpClient Client { get; }

    public Dictionary<string, string> Env { get; } = new();

    public static IEnumerable<string> AssetNames(string tag) =>
    [
        $"llama-{tag}-bin-win-cuda-12.4-x64.zip",
        $"llama-{tag}-bin-win-cuda-13.5-x64.zip",
        "cudart-llama-bin-win-cuda-12.4-x64.zip",
        "cudart-llama-bin-win-cuda-13.5-x64.zip",
        $"llama-{tag}-bin-win-vulkan-x64.zip",
        $"llama-{tag}-bin-win-cpu-x64.zip",
        $"llama-{tag}-bin-win-sycl-x64.zip",
    ];

    // The newest release is a non-build tag without Windows binaries, as
    // releases/latest really returns.
    public static string ReleasesJson(params string[] tags) => JsonSerializer.Serialize(
        new object[] { new { tag_name = "v0.9.0", assets = Array.Empty<object>() } }
            .Concat(tags.Select(t => new
            {
                tag_name = t,
                assets = AssetNames(t).Select(n => new { name = n, browser_download_url = "http://dl.test/" + n, size = 1 }),
            })));

    public static byte[] Zip(string assetName)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create))
        {
            var names = assetName.StartsWith("cudart", StringComparison.Ordinal)
                ? new[] { "cudart64_12.dll", "cublas64_12.dll" }
                : [LlamaCppConstants.ServerExecutableName, "llama.dll", "ggml-base.dll"];
            foreach (var name in names)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(assetName);
            }
        }

        return buffer.ToArray();
    }

    public void PlaceAllModels()
    {
        var ladder = ModelLadder.Load(Path.Combine(Repo, "scripts", "models.json"));
        Directory.CreateDirectory(ModelsDir);
        foreach (var rung in ladder.All)
        {
            File.WriteAllText(Path.Combine(ModelsDir, rung.File), "gguf");
            if (rung.Mmproj is not null)
            {
                File.WriteAllText(Path.Combine(ModelsDir, rung.Mmproj), "gguf");
            }
        }
    }

    public void RemoveMmprojs()
    {
        foreach (var file in Directory.GetFiles(ModelsDir, "mmproj-*"))
        {
            File.Delete(file);
        }
    }

    public void WriteSettings(string json) =>
        File.WriteAllText(Path.Combine(Home, ".openmono", "settings.json"), json);

    public LauncherDeps Deps() => new(
        Runner, Client, Gpu, Home, key => Env.GetValueOrDefault(key), Host, (_, _) => Task.CompletedTask, 3);

    public InferenceOrchestrator Orchestrator() => new(Deps());

    public FakeServerHost.Launch LastLaunch => Host.Launches[^1];

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
    public async Task TwentyFourGbInstallsTheCudaBuildAndServesTheLinuxTopTier()
    {
        using var rig = new LlamaRig();

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        var state = result.State!;
        state.Backend.Should().Be(InferenceState.LlamaBackend);
        state.Endpoint.Should().Be("http://127.0.0.1:7474");
        state.ModelAlias.Should().Be(LlamaRig.Top);
        state.AgentModel.Should().Be(LlamaRig.Top);
        state.Accel.Should().Be("cuda");
        state.LlamaBuild.Should().Be("b9000-cuda-12.4");
        state.ContextTokens.Should().Be(172032);
        state.VisionEnabled.Should().BeTrue();
        state.StartedByUs.Should().BeTrue();
        state.ServerPid.Should().Be(rig.LastLaunch.Pid);

        var dir = Path.Combine(rig.InstallRoot, "b9000-cuda-12.4");
        rig.LastLaunch.Executable.Should().Be(Path.Combine(dir, LlamaCppConstants.ServerExecutableName));
        File.Exists(Path.Combine(dir, "cudart64_12.dll")).Should().BeTrue();
        rig.Http.Requests.Should().NotContain(r => r.Contains("cuda-13.5") || r.Contains("sycl") || r.Contains("vulkan"));

        var args = rig.LastLaunch;
        args.Value("--model").Should().Be(Path.Combine(rig.ModelsDir, LlamaRig.Top + ".gguf"));
        args.Value("--alias").Should().Be(LlamaRig.Top);
        args.Value("--host").Should().Be("127.0.0.1");
        args.Value("--port").Should().Be("7474");
        args.Value("--n-gpu-layers").Should().Be("99");
        args.Value("--cache-type-k").Should().Be("q8_0");
        args.Value("--mmproj").Should().Be(Path.Combine(rig.ModelsDir, "mmproj-qwen3.8-27B-F16.gguf"));
        args.Arguments.Should().Contain(["--jinja", "--metrics"]);

        rig.Orchestrator().Store.Load().Should().BeEquivalentTo(state);
    }

    [Theory]
    [InlineData(32, LlamaRig.Top)]
    [InlineData(24, LlamaRig.Top)]
    [InlineData(16, "Qwen3.5-9B-UD-Q4_K_XL")]
    [InlineData(12, "Qwen3.5-9B-UD-Q4_K_XL")]
    [InlineData(8, "Qwen3.5-4B-UD-Q4_K_XL")]
    [InlineData(6, "Qwen3.5-2B-UD-Q4_K_XL")]
    [InlineData(4, "Qwen3.5-2B-UD-Q4_K_XL")]
    [InlineData(2, "Qwen3.5-0.8B-UD-Q4_K_XL")]
    public async Task NvidiaCardsFollowTheWindowsLadder(long vramGiB, string alias)
    {
        using var rig = new LlamaRig(vramGiB);

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        result.State!.ModelAlias.Should().Be(alias);
        result.State.Accel.Should().Be("cuda");
        rig.LastLaunch.Value("--n-gpu-layers").Should().Be("99");
    }

    [Fact]
    public async Task NoNvidiaGpuServesTheTwoBOnTheCpuBuild()
    {
        using var rig = new LlamaRig(noGpu: true);

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        result.State!.ModelAlias.Should().Be(LlamaRig.Cpu);
        result.State.Accel.Should().Be("cpu");
        result.State.LlamaBuild.Should().Be("b9000-cpu");
        result.State.ContextTokens.Should().Be(65536);
        rig.LastLaunch.Arguments.Should().NotContain("--n-gpu-layers");
        rig.LastLaunch.Arguments.Should().Contain("--threads-batch");
        rig.LastLaunch.Value("--cache-type-k").Should().Be("q8_0");
        rig.Host.Launches.Should().HaveCount(1);
        rig.Http.Requests.Should().NotContain(r => r.Contains("cuda") || r.Contains("vulkan"));
        rig.Runner.Calls.Should().NotContain(c => c.FileName == "nvidia-smi");
    }

    [Fact]
    public async Task DriverOlderThanEveryCudaBuildUsesVulkanOnTheGpu()
    {
        using var rig = new LlamaRig(vramGiB: 12, driverCuda: "11.8");

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        result.State!.Accel.Should().Be("vulkan");
        result.State.LlamaBuild.Should().Be("b9000-vulkan");
        result.State.ModelAlias.Should().Be("Qwen3.5-9B-UD-Q4_K_XL");
        rig.LastLaunch.Value("--n-gpu-layers").Should().Be("99");
    }

    [Fact]
    public async Task NewerDriverPicksTheNewerCudaBuild()
    {
        using var rig = new LlamaRig(driverCuda: "13.6");

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.State!.LlamaBuild.Should().Be("b9000-cuda-13.5");
    }

    [Fact]
    public async Task CrashingGpuBuildsFallBackToTheCpuModel()
    {
        using var rig = new LlamaRig();
        rig.Host.Crashes = exe => !exe.Contains("-cpu", StringComparison.Ordinal);

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        rig.Host.Launches.Select(l => Path.GetFileName(Path.GetDirectoryName(l.Executable)))
            .Should().Equal("b9000-cuda-12.4", "b9000-vulkan", "b9000-cpu");
        result.State!.Accel.Should().Be("cpu");
        result.State.ModelAlias.Should().Be(LlamaRig.Cpu);
        result.Log.Count(l => l.StartsWith("WARNING: the llama.cpp", StringComparison.Ordinal)).Should().Be(2);
    }

    [Fact]
    public async Task OurServerWithTheSameSettingsIsReused()
    {
        using var rig = new LlamaRig();
        var orchestrator = rig.Orchestrator();

        await orchestrator.UpAsync(new UpOptions());
        var second = await orchestrator.UpAsync(new UpOptions());

        second.Ok.Should().BeTrue(second.Error);
        rig.Host.Launches.Should().HaveCount(1);
        second.State!.StartedByUs.Should().BeTrue();
        second.Log.Should().Contain(l => l.Contains("already serves " + LlamaRig.Top));
    }

    [Fact]
    public async Task InstalledBuildIsNotDownloadedAgain()
    {
        using var rig = new LlamaRig();
        await rig.Orchestrator().UpAsync(new UpOptions());
        await rig.Orchestrator().DownAsync(force: false);
        var downloads = rig.Http.Requests.Count(r => r.Contains("dl.test"));

        var again = await rig.Orchestrator().UpAsync(new UpOptions());

        again.Ok.Should().BeTrue(again.Error);
        rig.Http.Requests.Count(r => r.Contains("dl.test")).Should().Be(downloads);
        rig.Http.Requests.Count(r => r.Contains("api.test")).Should().Be(1);
    }

    [Fact]
    public async Task UpdateInstallsANewerRelease()
    {
        using var rig = new LlamaRig();
        await rig.Orchestrator().UpAsync(new UpOptions());
        rig.Http.RespondJson("api.test/releases", () => LlamaRig.ReleasesJson("b9100", LlamaRig.Tag));
        foreach (var asset in LlamaRig.AssetNames("b9100"))
        {
            rig.Http.RespondBytes("dl.test/" + asset, () => LlamaRig.Zip(asset));
        }

        var updated = await rig.Orchestrator().UpAsync(new UpOptions(Update: true));

        updated.Ok.Should().BeTrue(updated.Error);
        updated.State!.LlamaBuild.Should().Be("b9100-cuda-12.4");
        rig.Host.Stopped.Should().HaveCount(1);
        rig.Host.Launches.Should().HaveCount(2);
    }

    [Fact]
    public async Task AnotherModelRestartsOurServer()
    {
        using var rig = new LlamaRig();
        var orchestrator = rig.Orchestrator();
        await orchestrator.UpAsync(new UpOptions());

        var switched = await orchestrator.UpAsync(new UpOptions(ModelOverride: "Qwen3.5-9B-UD-Q4_K_XL"));

        switched.Ok.Should().BeTrue(switched.Error);
        rig.Host.Stopped.Should().Equal(rig.Host.Launches[0].Pid);
        rig.LastLaunch.Value("--alias").Should().Be("Qwen3.5-9B-UD-Q4_K_XL");
        switched.Log.Should().Contain(l => l.StartsWith("Restarting llama-server", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OurServerIsFoundEvenWithoutTheStateFile()
    {
        using var rig = new LlamaRig();
        await rig.Orchestrator().UpAsync(new UpOptions());
        rig.Orchestrator().Store.Delete();

        var cpu = await rig.Orchestrator().UpAsync(new UpOptions(ModelOverride: LlamaRig.Cpu));

        cpu.Ok.Should().BeTrue(cpu.Error);
        rig.Host.Stopped.Should().Equal(rig.Host.Launches[0].Pid);
        rig.Llama.Alias.Should().Be(LlamaRig.Cpu);
    }

    [Fact]
    public async Task ForeignServerWithAnotherModelIsNeverReplaced()
    {
        using var rig = new LlamaRig();
        rig.Llama.Serve("Llama-3.3-70B-Instruct-Q4_K_M");

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("Port 7474 is taken by a server OpenMono did not start")
            .And.Contain("Llama-3.3-70B-Instruct-Q4_K_M").And.Contain("Stop that server");
        rig.Host.Launches.Should().BeEmpty();
        rig.Host.Stopped.Should().BeEmpty();
    }

    [Fact]
    public async Task ForeignServerWithTheLadderModelIsAdoptedAndLeftRunning()
    {
        using var rig = new LlamaRig();
        rig.Llama.Serve(LlamaRig.Top);

        var result = await rig.Orchestrator().UpAsync(new UpOptions());
        var down = await rig.Orchestrator().DownAsync(force: false);

        result.Ok.Should().BeTrue(result.Error);
        result.State!.StartedByUs.Should().BeFalse();
        result.Log.Should().Contain(l => l.Contains("adopting it"));
        rig.Host.Launches.Should().BeEmpty();
        down.Stopped.Should().BeFalse();
        down.Message.Should().Contain("not started by OpenMono");
        rig.Llama.Alias.Should().Be(LlamaRig.Top);
    }

    [Fact]
    public async Task DownStopsTheServerWeStarted()
    {
        using var rig = new LlamaRig();
        var orchestrator = rig.Orchestrator();
        await orchestrator.UpAsync(new UpOptions());

        var down = await orchestrator.DownAsync(force: false);

        down.Stopped.Should().BeTrue();
        rig.Llama.Alias.Should().BeNull();
        orchestrator.Store.Load().Should().BeNull();
    }

    [Fact]
    public async Task VisionOffInSettingsLoadsNoProjector()
    {
        using var rig = new LlamaRig();
        rig.WriteSettings("""{"inference":{"vision_enabled":false}}""");

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.State!.VisionEnabled.Should().BeFalse();
        result.State.ContextTokens.Should().Be(196608);
        rig.LastLaunch.Arguments.Should().NotContain("--mmproj");
    }

    [Fact]
    public async Task MissingProjectorTurnsVisionOffWithoutFailing()
    {
        using var rig = new LlamaRig(vramGiB: 8);
        rig.RemoveMmprojs();

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        result.State!.VisionEnabled.Should().BeFalse();
        result.State.ContextTokens.Should().Be(131072);
        result.Log.Should().Contain(l => l.StartsWith("mmproj download failed", StringComparison.Ordinal));
        result.Log.Should().Contain(l => l.StartsWith("Vision off:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VisionSessionWithoutProjectorFails()
    {
        using var rig = new LlamaRig();
        rig.RemoveMmprojs();

        var result = await rig.Orchestrator().UpAsync(new UpOptions(Vision: true));

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("vision projector").And.Contain("images were not sent");
    }

    [Fact]
    public async Task ModelOverrideOutsideTheLadderIsRefused()
    {
        using var rig = new LlamaRig();

        var result = await rig.Orchestrator().UpAsync(new UpOptions(ModelOverride: "mistral-7b"));

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("not in the Windows model ladder").And.Contain(LlamaRig.Top);
        rig.Host.Launches.Should().BeEmpty();
    }

    [Fact]
    public async Task LadderModelInSettingsIsServed()
    {
        using var rig = new LlamaRig();
        rig.WriteSettings("""{"llm":{"model":"Qwen3.5-4B-UD-Q4_K_XL"}}""");

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.State!.ModelAlias.Should().Be("Qwen3.5-4B-UD-Q4_K_XL");
        result.State.AgentModel.Should().Be("Qwen3.5-4B-UD-Q4_K_XL");
    }

    [Fact]
    public async Task OtherModelInSettingsIsLeftToTheAgent()
    {
        using var rig = new LlamaRig();
        rig.WriteSettings("""{"llm":{"model":"my-model"}}""");

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.State!.ModelAlias.Should().Be(LlamaRig.Top);
        result.State.AgentModel.Should().BeNull();
    }

    [Fact]
    public async Task ApiKeyReachesTheServerButNotTheStateFile()
    {
        using var rig = new LlamaRig();
        rig.Env[LlamaCppConstants.ApiKeyEnvVar] = "s3cret-key";
        var orchestrator = rig.Orchestrator();

        await orchestrator.UpAsync(new UpOptions());

        rig.LastLaunch.Value("--api-key").Should().Be("s3cret-key");
        File.ReadAllText(orchestrator.Store.FilePath).Should().NotContain("s3cret-key");
    }

    [Fact]
    public async Task RemoteEndpointIsUsedAsIs()
    {
        using var rig = new LlamaRig();
        rig.WriteSettings("""{"llm":{"endpoint":"http://gpu-box.lan:7474","model":"big"}}""");

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeTrue();
        result.State!.Backend.Should().Be(InferenceState.RemoteBackend);
        result.State.Endpoint.Should().Be("http://gpu-box.lan:7474");
        result.State.AgentModel.Should().Be("big");
        rig.Host.Launches.Should().BeEmpty();
        rig.Http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task EnvironmentEndpointWinsOverSettings()
    {
        using var rig = new LlamaRig();
        rig.WriteSettings("""{"llm":{"endpoint":"http://127.0.0.1:7474"}}""");
        rig.Env["OPENMONO_ENDPOINT"] = "https://api.example.com/v1";

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.State!.Endpoint.Should().Be("https://api.example.com/v1");
    }

    [Fact]
    public async Task CorruptSettingsStopEarly()
    {
        using var rig = new LlamaRig();
        rig.WriteSettings("{not json");

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("settings.json is corrupt");
    }

    [Fact]
    public async Task NoReleaseReachableAndNothingInstalledFails()
    {
        using var rig = new LlamaRig(noGpu: true);
        rig.Http.Route("api.test/releases", () => throw new HttpRequestException("offline"));

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("No llama.cpp release").And.Contain("github.com/ggml-org/llama.cpp/releases");
    }

    [Fact]
    public async Task MissingModelCatalogPointsAtTheCheckout()
    {
        using var rig = new LlamaRig();
        rig.Env[LlamaCppConstants.RepoDirEnvVar] = Path.Combine(rig.Home, "nowhere");

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("models.json");
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
    public void TwoWordCommands()
    {
        LauncherArgs.Parse(["inference", "up", "--update"]).Command.Should().Be("inference up");
        LauncherArgs.Parse(["models", "check"]).Command.Should().Be("models check");
        LauncherArgs.Parse(["models", "--json"]).Command.Should().Be("models");
        LauncherArgs.Parse([]).Command.Should().Be("help");
    }

    [Fact]
    public async Task AgentPlanEmitsComposeEnvironmentAndArgs()
    {
        using var rig = new LlamaRig();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await LauncherCli.RunAsync(
            ["agent-plan", "--workdir", "C:\\proj", "--compose", "C:\\oma\\docker\\docker-compose.yml", "--no-git-auth", "--", "--acp-only"],
            rig.Deps(), stdout, stderr);

        code.Should().Be(0, stderr.ToString());
        using var doc = JsonDocument.Parse(stdout.ToString());
        var root = doc.RootElement;
        var env = root.GetProperty("environment");
        env.GetProperty("OPENMONO_ENDPOINT").GetString().Should().Be("http://host.docker.internal:7474");
        env.GetProperty("CTX_SIZE").GetString().Should().Be("172032");
        env.GetProperty("OPENMONO_VISION_ENABLED").GetString().Should().Be("1");
        root.GetProperty("acp_port").GetInt32().Should().Be(7475);
        var args = root.GetProperty("docker_args").EnumerateArray().Select(e => e.GetString()).ToList();
        args.Should().Contain("127.0.0.1:7475:7475");
        args.Should().Contain("OPENMONO_MODEL=" + LlamaRig.Top);
        args.Should().NotContain(a => a!.StartsWith("OPENMONO_LLM_BACKEND", StringComparison.Ordinal)
            || a.StartsWith("OPENMONO_TOOL_CALLING", StringComparison.Ordinal));
        args.TakeLast(2).Should().Equal("agent", "--acp-only");
        root.GetProperty("inference").GetProperty("llama_build").GetString().Should().Be("b9000-cuda-12.4");
    }

    [Fact]
    public async Task AgentArgsCanArriveAsJsonEnv()
    {
        using var rig = new LlamaRig();
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
        using var rig = new LlamaRig();
        rig.Llama.Serve("someone-elses-model");
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await LauncherCli.RunAsync(
            ["agent-plan", "--workdir", "C:\\proj", "--compose", "c.yml"], rig.Deps(), stdout, stderr);

        code.Should().Be(1);
        stdout.ToString().Should().BeEmpty();
        stderr.ToString().Should().Contain("Port 7474 is taken");
    }

    [Fact]
    public async Task ModelsListsTheLadderAndThisMachinesPick()
    {
        using var rig = new LlamaRig(vramGiB: 8);
        var stdout = new StringWriter();

        var code = await LauncherCli.RunAsync(["models"], rig.Deps(), stdout, new StringWriter());

        code.Should().Be(0);
        var text = stdout.ToString();
        text.Should().Contain("Qwen3.8-27B-UD-Q4_K_M").And.Contain("Qwen3.5-0.8B-UD-Q4_K_XL");
        text.Should().Contain("This machine (NVIDIA GeForce RTX 4090, 8 GB): Qwen3.5-4B-UD-Q4_K_XL, llama.cpp backends in order: CUDA, then Vulkan, then CPU.");
        rig.Host.Launches.Should().BeEmpty();
        rig.Http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task InferenceUpReportsTheServedModel()
    {
        using var rig = new LlamaRig(noGpu: true);
        var stdout = new StringWriter();

        var code = await LauncherCli.RunAsync(["inference", "up"], rig.Deps(), stdout, new StringWriter());

        code.Should().Be(0);
        stdout.ToString().Should().Contain($"Inference ready: http://127.0.0.1:7474 (model {LlamaRig.Cpu}, llama.cpp b9000-cpu, vision on).");
    }

    [Fact]
    public async Task UnknownCommandIsUsageError()
    {
        using var rig = new LlamaRig();

        var code = await LauncherCli.RunAsync(["frobnicate"], rig.Deps(), new StringWriter(), new StringWriter());

        code.Should().Be(2);
    }
}

public sealed class DoctorRunnerTests
{
    private const string RouteCheck =
        "run --rm --add-host host.docker.internal:host-gateway --entrypoint curl openmono-agent:latest -fsS -m 5 http://host.docker.internal:7474/health";

    private static DoctorInput Input(bool isWindows = false) => new(
        WorkDir: "C:\\proj",
        IsWindows: isWindows,
        PathDirectories: ["C:\\Windows\\System32\\WindowsPowerShell\\v1.0"],
        FileExists: p => p.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase),
        DirectoryHasEntries: _ => false,
        ListFiles: _ => []);

    private static void DockerUp(LlamaRig rig)
    {
        rig.Runner.Respond("docker", "version --format {{.Server.Version}}", 0, "28.1.1\n");
        rig.Runner.Respond("docker", "compose version --short", 0, "2.35.1\n");
        rig.Runner.Respond("docker", "image inspect openmono-agent:latest --format {{.Id}}", 0, "sha256:abc\n");
    }

    [Fact]
    public async Task MissingDockerFails()
    {
        using var rig = new LlamaRig();

        var checks = await new DoctorRunner(rig.Deps()).RunAsync(Input());

        checks.Should().Contain(c => c.Name == "docker" && c.Level == CheckLevel.Fail);
        DoctorRunner.ExitCode(checks).Should().Be(1);
    }

    [Fact]
    public async Task HealthyMachinePasses()
    {
        using var rig = new LlamaRig();
        await rig.Orchestrator().UpAsync(new UpOptions());
        rig.Runner.Respond("git", "--version", 0, "git version 2.51.0.windows.1\n");
        rig.Runner.Respond("dotnet", "--list-sdks", 0, "10.0.403 [C:\\Program Files\\dotnet\\sdk]\n");
        DockerUp(rig);
        rig.Runner.Respond("docker", RouteCheck, 0, "{\"status\":\"ok\"}");

        var checks = await new DoctorRunner(rig.Deps()).RunAsync(Input(isWindows: true));

        checks.Where(c => c.Level == CheckLevel.Fail).Should().BeEmpty();
        checks.Should().Contain(c => c.Name == "host shell" && c.Detail.Contains("powershell.exe"));
        checks.Should().Contain(c => c.Name == "gpu" && c.Level == CheckLevel.Ok
            && c.Detail.Contains("serves " + LlamaRig.Top) && c.Detail.Contains("CUDA, then Vulkan, then CPU"));
        checks.Should().Contain(c => c.Name == "llama.cpp" && c.Level == CheckLevel.Ok && c.Detail.StartsWith("llama.cpp b9000-cuda-12.4 installed"));
        checks.Should().Contain(c => c.Name == "model" && c.Level == CheckLevel.Ok && c.Detail.Contains("with its vision projector"));
        checks.Should().Contain(c => c.Name == "inference server" && c.Level == CheckLevel.Ok && c.Detail.Contains(LlamaRig.Top));
        checks.Should().Contain(c => c.Name == "container route" && c.Level == CheckLevel.Ok);
        checks.Should().Contain(c => c.Name == "acp" && c.Level == CheckLevel.Info);
        checks.Should().Contain(c => c.Name == "git" && c.Level == CheckLevel.Ok && c.Detail == "Git 2.51.0.");
        checks.Should().Contain(c => c.Name == "dotnet sdk" && c.Level == CheckLevel.Ok && c.Detail.StartsWith(".NET SDK 10.0.403"));
        DoctorRunner.ExitCode(checks).Should().Be(0);
    }

    [Fact]
    public async Task MissingGitAndOldSdkPointAtSetup()
    {
        using var rig = new LlamaRig();
        rig.Runner.Respond("dotnet", "--list-sdks", 0, "9.0.300 [C:\\Program Files\\dotnet\\sdk]\n");

        var checks = await new DoctorRunner(rig.Deps()).RunAsync(Input());

        checks.Should().Contain(c => c.Name == "git" && c.Level == CheckLevel.Warn && c.Detail == "Git not found."
            && c.Fix!.Contains("setup: it installs Git (winget Git.Git"));
        checks.Should().Contain(c => c.Name == "dotnet sdk" && c.Level == CheckLevel.Fail
            && c.Detail.Contains("9.0.300") && c.Fix!.Contains("winget Microsoft.DotNet.SDK.10"));
    }

    [Fact]
    public async Task FreshMachinePointsAtSetupForLlamaCppAndTheModel()
    {
        using var rig = new LlamaRig(noGpu: true);
        foreach (var file in Directory.GetFiles(rig.ModelsDir))
        {
            File.Delete(file);
        }

        var checks = await new DoctorRunner(rig.Deps()).RunAsync(Input());

        checks.Should().Contain(c => c.Name == "gpu" && c.Level == CheckLevel.Info && c.Detail.Contains("CPU-only, serves " + LlamaRig.Cpu));
        checks.Should().Contain(c => c.Name == "llama.cpp" && c.Level == CheckLevel.Warn && c.Fix!.Contains("setup"));
        checks.Should().Contain(c => c.Name == "model" && c.Level == CheckLevel.Warn && c.Detail.StartsWith(LlamaRig.Cpu + ".gguf"));
        rig.Http.Requests.Should().NotContain(r => r.Contains("dl.test") || r.Contains("api.test"));
    }

    [Fact]
    public async Task BlockedContainerRouteFails()
    {
        using var rig = new LlamaRig();
        await rig.Orchestrator().UpAsync(new UpOptions());
        DockerUp(rig);
        rig.Runner.Respond("docker", RouteCheck, 7, "", "curl: (7) Failed to connect");

        var checks = await new DoctorRunner(rig.Deps()).RunAsync(Input());

        checks.Should().Contain(c => c.Name == "container route" && c.Level == CheckLevel.Fail);
    }

    [Fact]
    public async Task RemoteEndpointSkipsLlamaChecks()
    {
        using var rig = new LlamaRig();
        rig.WriteSettings("""{"llm":{"endpoint":"http://gpu-box.lan:7474"}}""");

        var checks = await new DoctorRunner(rig.Deps()).RunAsync(Input());

        checks.Should().NotContain(c => c.Name == "llama.cpp" || c.Name == "model");
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
