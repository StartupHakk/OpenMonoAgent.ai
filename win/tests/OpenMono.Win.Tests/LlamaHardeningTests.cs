using System.Text.Json;
using FluentAssertions;
using OpenMono.Win.Inference;
using OpenMono.Win.Launcher;
using Xunit;

namespace OpenMono.Win.Tests;

// Backend pick, install and download failures, server start and health wait,
// fallback, and stop semantics of the Windows llama.cpp path, end to end
// through the orchestrator with the LlamaRig fakes.
public sealed class BackendPickTests
{
    [Fact]
    public async Task MissingNvidiaSmiOnAnNvidiaMachineUsesVulkan()
    {
        using var rig = new LlamaRig(vramGiB: 12);
        rig.Runner.Respond("nvidia-smi", "", 9009, "", "'nvidia-smi' is not recognized as an internal or external command");

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        result.State!.Accel.Should().Be("vulkan");
        result.State.ModelAlias.Should().Be("Qwen3.5-9B-UD-Q4_K_XL");
        rig.LastLaunch.Value("--n-gpu-layers").Should().Be("99");
        rig.Http.Requests.Should().NotContain(r => r.Contains("cuda"));
        result.Log.Should().Contain(l => l.Contains("driver CUDA unknown"));
    }

    [Fact]
    public async Task NvidiaSmiThatCannotRunIsTreatedLikeMissing()
    {
        using var rig = new LlamaRig(vramGiB: 8);
        rig.Runner.OnCall("nvidia-smi", "", () => throw new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified"));

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        result.State!.Accel.Should().Be("vulkan");
        result.State.ModelAlias.Should().Be("Qwen3.5-4B-UD-Q4_K_XL");
    }

    [Fact]
    public async Task BannerWithoutACudaVersionUsesVulkan()
    {
        using var rig = new LlamaRig();
        rig.Runner.Respond("nvidia-smi", "", 0, "| NVIDIA-SMI 572.16    Driver Version: 572.16 |\n");

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.State!.Accel.Should().Be("vulkan");
        result.State.ModelAlias.Should().Be(LlamaRig.Top);
    }

    [Fact]
    public async Task ReleaseWithoutTheCudaRuntimeZipSkipsCuda()
    {
        using var rig = new LlamaRig();
        rig.Http.RespondJson("api.test/releases", () => JsonSerializer.Serialize(new[]
        {
            new
            {
                tag_name = LlamaRig.Tag,
                assets = LlamaRig.AssetNames(LlamaRig.Tag).Where(n => !n.StartsWith("cudart", StringComparison.Ordinal))
                    .Select(n => new { name = n, browser_download_url = "http://dl.test/" + n, size = 1 }),
            },
        }));

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        result.State!.LlamaBuild.Should().Be("b9000-vulkan");
        result.Log.Should().Contain(l => l.StartsWith("llama.cpp CUDA build unavailable", StringComparison.Ordinal));
    }

    // Real cards report a little under their marketed size; every class is
    // matched from 1 GiB below nominal.
    [Theory]
    [InlineData(32607, LlamaRig.Top)]            // RTX 5090
    [InlineData(24564, LlamaRig.Top)]            // RTX 4090
    [InlineData(23552, LlamaRig.Top)]            // exactly 23 GiB
    [InlineData(23551, "Qwen3.5-9B-UD-Q4_K_XL")]
    [InlineData(16376, "Qwen3.5-9B-UD-Q4_K_XL")] // RTX 4080
    [InlineData(12288, "Qwen3.5-9B-UD-Q4_K_XL")] // RTX 3060 12 GB
    [InlineData(11264, "Qwen3.5-9B-UD-Q4_K_XL")]
    [InlineData(10240, "Qwen3.5-4B-UD-Q4_K_XL")] // RTX 3080 10 GB
    [InlineData(8188, "Qwen3.5-4B-UD-Q4_K_XL")]  // RTX 4060
    [InlineData(7168, "Qwen3.5-4B-UD-Q4_K_XL")]
    [InlineData(6144, "Qwen3.5-2B-UD-Q4_K_XL")]  // RTX 2060
    [InlineData(4096, "Qwen3.5-2B-UD-Q4_K_XL")]  // GTX 1650
    [InlineData(3072, "Qwen3.5-2B-UD-Q4_K_XL")]
    [InlineData(3071, "Qwen3.5-0.8B-UD-Q4_K_XL")]
    [InlineData(2048, "Qwen3.5-0.8B-UD-Q4_K_XL")]
    [InlineData(0, "Qwen3.5-0.8B-UD-Q4_K_XL")]
    public async Task VramBoundariesInMiB(long mib, string alias)
    {
        using var rig = new LlamaRig();
        rig.Gpu = new FakeGpuProber(new GpuInfo("NVIDIA GeForce", mib * 1024 * 1024, true));

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        result.State!.ModelAlias.Should().Be(alias);
        result.State.Accel.Should().Be("cuda");
    }

    [Fact]
    public async Task TheBiggestNvidiaCardDecidesNotAnIntegratedGpu()
    {
        using var rig = new LlamaRig();
        rig.Gpu = new FakeGpuProber(
            new GpuInfo("Intel(R) UHD Graphics 770", 0, false),
            new GpuInfo("NVIDIA GeForce RTX 3060", 12288L * 1024 * 1024, true),
            new GpuInfo("NVIDIA GeForce RTX 4090", 24564L * 1024 * 1024, true));

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.State!.ModelAlias.Should().Be(LlamaRig.Top);
    }

    [Fact]
    public async Task ForcingTheTwoBOnTheCpuUsesTheCpuRungSettings()
    {
        using var rig = new LlamaRig(noGpu: true);

        var result = await rig.Orchestrator().UpAsync(new UpOptions(ModelOverride: LlamaRig.Cpu));

        result.Ok.Should().BeTrue(result.Error);
        rig.LastLaunch.Value("--cache-type-k").Should().Be("q8_0");
        rig.LastLaunch.Arguments.Should().Contain("--threads-batch");
        result.Log.Should().Contain(l => l.Contains("[no NVIDIA GPU, CPU]"));
    }

    [Fact]
    public async Task ForcedTwoBFallingBackToTheCpuBuildGetsTheCpuRungSettings()
    {
        using var rig = new LlamaRig();
        rig.Host.Crashes = exe => !exe.Contains("-cpu", StringComparison.Ordinal);

        var result = await rig.Orchestrator().UpAsync(new UpOptions(ModelOverride: LlamaRig.Cpu));

        result.Ok.Should().BeTrue(result.Error);
        rig.Host.Launches[0].Value("--cache-type-k").Should().Be("q4_0");
        rig.LastLaunch.Value("--cache-type-k").Should().Be("q8_0");
    }
}

public sealed class InstallAndDownloadFailureTests
{
    [Fact]
    public async Task CorruptCudaZipFallsBackToVulkanAndLeavesNoHalfInstall()
    {
        using var rig = new LlamaRig();
        rig.Http.RespondBytes("dl.test/llama-b9000-bin-win-cuda-12.4-x64.zip",
            () => System.Text.Encoding.ASCII.GetBytes("<html><body>502 Bad Gateway from the CDN</body></html>"));

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        result.State!.LlamaBuild.Should().Be("b9000-vulkan");
        result.Log.Should().Contain(l => l.Contains("Extracting llama-b9000-bin-win-cuda-12.4-x64.zip failed"));
        LlamaCppInstaller.ListInstalled(rig.InstallRoot).Select(b => b.Id).Should().Equal("b9000-vulkan");
        Directory.GetFiles(Path.Combine(rig.InstallRoot, "downloads")).Should().BeEmpty();
    }

    [Fact]
    public async Task ZipWithoutLlamaServerIsNotInstalled()
    {
        using var rig = new LlamaRig(noGpu: true);
        rig.Http.RespondBytes("dl.test/llama-b9000-bin-win-cpu-x64.zip", () => LlamaRig.Zip("cudart-llama-bin-win-cuda-12.4-x64.zip"));

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("has no llama-server");
        rig.Host.Launches.Should().BeEmpty();
        LlamaCppInstaller.ListInstalled(rig.InstallRoot).Should().BeEmpty();
    }

    [Fact]
    public async Task EveryBuildDownloadFailingFailsWithoutStartingAnything()
    {
        using var rig = new LlamaRig();
        foreach (var asset in LlamaRig.AssetNames(LlamaRig.Tag))
        {
            rig.Http.Route("dl.test/" + asset, () => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("llama-b9000-bin-win-cpu-x64.zip failed").And.Contain("returned 404");
        result.Log.Should().Contain(l => l.StartsWith("llama.cpp CUDA build unavailable", StringComparison.Ordinal));
        result.Log.Should().Contain(l => l.StartsWith("llama.cpp Vulkan build unavailable", StringComparison.Ordinal));
        rig.Host.Launches.Should().BeEmpty();
        rig.Orchestrator().Store.Load().Should().BeNull();
    }

    [Fact]
    public async Task FailedUpdateKeepsTheInstalledBuildAndSaysSo()
    {
        using var rig = new LlamaRig();
        await rig.Orchestrator().UpAsync(new UpOptions());
        rig.Http.RespondJson("api.test/releases", () => LlamaRig.ReleasesJson("b9100", LlamaRig.Tag));

        var updated = await rig.Orchestrator().UpAsync(new UpOptions(Update: true));

        updated.Ok.Should().BeTrue(updated.Error);
        updated.State!.LlamaBuild.Should().Be("b9000-cuda-12.4");
        updated.Log.Should().Contain(l => l.StartsWith("WARNING: Downloading llama-b9100-bin-win-cuda-12.4-x64.zip failed", StringComparison.Ordinal)
            && l.EndsWith("Keeping llama.cpp b9000-cuda-12.4.", StringComparison.Ordinal));
        rig.Host.Launches.Should().HaveCount(1);
    }

    [Fact]
    public async Task OfflineUpdateKeepsTheInstalledBuild()
    {
        using var rig = new LlamaRig();
        await rig.Orchestrator().UpAsync(new UpOptions());
        rig.Http.Route("api.test/releases", () => throw new HttpRequestException("No such host is known."));

        var updated = await rig.Orchestrator().UpAsync(new UpOptions(Update: true));

        updated.Ok.Should().BeTrue(updated.Error);
        updated.Log.Should().Contain(l => l.StartsWith("Could not check for a newer llama.cpp build; keeping b9000-cuda-12.4", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ModelDownloadErrorStopsBeforeAnyServerStarts()
    {
        using var rig = new LlamaRig();
        File.Delete(Path.Combine(rig.ModelsDir, LlamaRig.Top + ".gguf"));

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("Model download failed")
            .And.Contain("huggingface.co/unsloth/Qwen3.8-27B-GGUF/resolve/main/Qwen3.8-27B-UD-Q4_K_M.gguf returned 404")
            .And.Contain("Run the same command again");
        File.Exists(Path.Combine(rig.ModelsDir, LlamaRig.Top + ".gguf")).Should().BeFalse();
        rig.Host.Launches.Should().BeEmpty();
    }

    [Fact]
    public async Task ModelErrorPageIsNotKeptAsTheModel()
    {
        using var rig = new LlamaRig(vramGiB: 4);
        File.Delete(Path.Combine(rig.ModelsDir, "Qwen3.5-2B-UD-Q4_K_XL.gguf"));
        rig.Http.RespondBytes("huggingface.co/unsloth/Qwen3.5-2B-GGUF/resolve/main/Qwen3.5-2B-UD-Q4_K_XL.gguf",
            () => System.Text.Encoding.UTF8.GetBytes("<!DOCTYPE html><title>Access to model restricted</title>"));

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("likely an HTTP error page");
        Directory.GetFiles(rig.ModelsDir, "Qwen3.5-2B-UD-Q4_K_XL.gguf*").Should().BeEmpty();
    }

    [Fact]
    public async Task FirstRunDownloadsTheModelAndProjectorFromTheLadderUrls()
    {
        using var rig = new LlamaRig(vramGiB: 8);
        foreach (var file in Directory.GetFiles(rig.ModelsDir))
        {
            File.Delete(file);
        }

        var gguf = new byte[ModelStore.MinModelBytes + 1];
        var mmproj = new byte[ModelStore.MinMmprojBytes + 1];
        rig.Http.RespondBytes("huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/main/Qwen3.5-4B-UD-Q4_K_XL.gguf", () => gguf);
        rig.Http.RespondBytes("huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/main/mmproj-F16.gguf", () => mmproj);

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        new FileInfo(Path.Combine(rig.ModelsDir, "Qwen3.5-4B-UD-Q4_K_XL.gguf")).Length.Should().Be(gguf.Length);
        new FileInfo(Path.Combine(rig.ModelsDir, "mmproj-qwen3.5-4B-F16.gguf")).Length.Should().Be(mmproj.Length);
        rig.LastLaunch.Value("--mmproj").Should().Be(Path.Combine(rig.ModelsDir, "mmproj-qwen3.5-4B-F16.gguf"));
        result.State!.VisionEnabled.Should().BeTrue();
        rig.Http.Requests.Should().NotContain(r => r.Contains("Qwen3.8") || r.Contains("Qwen3.5-9B") || r.Contains("Qwen3.5-2B"));
    }
}

public sealed class ServerStartTests
{
    [Fact]
    public async Task ServerBindsLoopbackOnTheDefaultPortOnly()
    {
        using var rig = new LlamaRig();

        await rig.Orchestrator().UpAsync(new UpOptions());

        rig.LastLaunch.Value("--host").Should().Be("127.0.0.1");
        rig.LastLaunch.Value("--port").Should().Be("7474");
        rig.LastLaunch.Arguments.Should().NotContain("0.0.0.0");
        rig.LastLaunch.Value("--log-file").Should().Be(Path.Combine(rig.Home, ".openmono", "win", "llama-server.log"));
    }

    [Fact]
    public async Task PortOverrideServesAndRecordsThatPort()
    {
        using var rig = new LlamaRig(port: 7480);

        var result = await rig.Orchestrator().UpAsync(new UpOptions(Port: 7480));

        result.Ok.Should().BeTrue(result.Error);
        rig.LastLaunch.Value("--port").Should().Be("7480");
        result.State!.Endpoint.Should().Be("http://127.0.0.1:7480");
        rig.Http.Requests.Should().Contain("GET 127.0.0.1:7480/health");
        rig.Http.Requests.Should().NotContain(r => r.Contains("127.0.0.1:7474"));
    }

    [Fact]
    public async Task SlowLoadIsWaitedForNotRestarted()
    {
        using var rig = new LlamaRig();
        rig.Host.HangsLoading = _ => true;
        rig.Llama.LoadingChecksLeft = 2;

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        rig.Host.Launches.Should().HaveCount(1);
        rig.Host.Stopped.Should().BeEmpty();
        rig.Llama.Alias.Should().Be(LlamaRig.Top);
    }

    [Fact]
    public async Task ServerThatNeverFinishesLoadingIsStoppedWithoutFallingBack()
    {
        using var rig = new LlamaRig();
        rig.Host.HangsLoading = _ => true;

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("did not become healthy on http://127.0.0.1:7474 in time").And.Contain("it was stopped");
        rig.Host.Launches.Should().HaveCount(1);
        rig.Host.Stopped.Should().Equal(rig.Host.Launches[0].Pid);
        rig.Llama.HealthChecks.Should().BeGreaterThanOrEqualTo(3);
        rig.Orchestrator().Store.Load().Should().BeNull();
    }

    [Fact]
    public void DefaultHealthWaitCoversASlowDiskLoad()
    {
        LlamaServerService.DefaultHealthAttempts.Should().BeGreaterThanOrEqualTo(600);
    }

    [Fact]
    public async Task CudaCrashFallsBackToVulkanOnTheSameGpuModel()
    {
        using var rig = new LlamaRig();
        rig.Host.Crashes = exe => exe.Contains("-cuda-", StringComparison.Ordinal);

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        result.State!.LlamaBuild.Should().Be("b9000-vulkan");
        result.State.ModelAlias.Should().Be(LlamaRig.Top);
        result.State.ContextTokens.Should().Be(172032);
        rig.LastLaunch.Value("--n-gpu-layers").Should().Be("99");
        result.Log.Should().Contain(l => l.StartsWith("WARNING: the llama.cpp CUDA build failed", StringComparison.Ordinal)
            && l.Contains("ggml_cuda_init: failed"));
    }

    [Fact]
    public async Task EveryBuildCrashingFailsWithTheServerLog()
    {
        using var rig = new LlamaRig();
        rig.Host.Crashes = _ => true;
        rig.Host.CrashLog = "llama_model_load: error loading model: tensor 'blk.0.attn_q.weight' data is not within the file bounds";

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("llama-server b9000-cpu exited while loading").And.Contain("data is not within the file bounds");
        rig.Host.Launches.Should().HaveCount(3);
        rig.Orchestrator().Store.Load().Should().BeNull();
    }

    [Fact]
    public async Task ServerThatDiedSinceTheLastStartIsStartedAgain()
    {
        using var rig = new LlamaRig();
        var orchestrator = rig.Orchestrator();
        var first = await orchestrator.UpAsync(new UpOptions());
        rig.Host.Die(first.State!.ServerPid!.Value);

        var second = await orchestrator.UpAsync(new UpOptions());

        second.Ok.Should().BeTrue(second.Error);
        rig.Host.Launches.Should().HaveCount(2);
        rig.Host.Stopped.Should().BeEmpty();
        second.State!.ServerPid.Should().Be(rig.Host.Launches[1].Pid);
    }

    [Fact]
    public async Task SettingsChangeRestartsOurServerWithTheNewFlags()
    {
        using var rig = new LlamaRig();
        var orchestrator = rig.Orchestrator();
        await orchestrator.UpAsync(new UpOptions());
        rig.Env[LlamaCppConstants.ThreadsEnvVar] = "6";

        var second = await orchestrator.UpAsync(new UpOptions());

        second.Ok.Should().BeTrue(second.Error);
        rig.Host.Stopped.Should().Equal(rig.Host.Launches[0].Pid);
        rig.LastLaunch.Value("--threads").Should().Be("6");
        second.Log.Should().Contain("Restarting llama-server with the new settings.");
    }

    [Fact]
    public async Task ForeignServerWithAnotherModelIsRefusedBeforeAnyDownload()
    {
        using var rig = new LlamaRig();
        foreach (var file in Directory.GetFiles(rig.ModelsDir))
        {
            File.Delete(file);
        }

        rig.Llama.Serve("Llama-3.3-70B-Instruct-Q4_K_M");

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("Port 7474 is taken").And.Contain("Llama-3.3-70B-Instruct-Q4_K_M").And.Contain(LlamaRig.Top);
        rig.Http.Requests.Should().NotContain(r => r.Contains("huggingface.co") || r.Contains("dl.test") || r.Contains("api.test"));
        rig.Host.Launches.Should().BeEmpty();
    }

    [Fact]
    public async Task OurOwnServerWithAnotherModelIsStillRestartedNotRefused()
    {
        using var rig = new LlamaRig();
        var orchestrator = rig.Orchestrator();
        await orchestrator.UpAsync(new UpOptions(ModelOverride: "Qwen3.5-9B-UD-Q4_K_XL"));
        orchestrator.Store.Delete();

        var result = await orchestrator.UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        rig.Llama.Alias.Should().Be(LlamaRig.Top);
        rig.Host.Stopped.Should().Equal(rig.Host.Launches[0].Pid);
    }

    [Fact]
    public async Task ForeignServerStillLoadingIsNotTakenOver()
    {
        using var rig = new LlamaRig();
        rig.Llama.Load(null);

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("Port 7474 is taken by a server OpenMono did not start (serving an unknown model");
        rig.Host.Launches.Should().BeEmpty();
    }

    [Fact]
    public async Task SomethingElseListeningOnThePortIsNotTakenOver()
    {
        using var rig = new LlamaRig();
        rig.Http.Route("127.0.0.1:7474/health", () => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        rig.Http.Route("127.0.0.1:7474/v1/models", () => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("Port 7474 is taken");
        rig.Host.Launches.Should().BeEmpty();
    }

    [Fact]
    public async Task ForeignServerServingOurFileNameIsAdopted()
    {
        using var rig = new LlamaRig();
        rig.Llama.Serve(LlamaRig.Top + ".gguf");

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        result.State!.StartedByUs.Should().BeFalse();
        rig.Host.Launches.Should().BeEmpty();
    }
}

public sealed class DeviceCheckTests
{
    private const string CudaListing = """
        ggml_cuda_init: found 1 CUDA devices:
          Device 0: NVIDIA GeForce RTX 4090, compute capability 8.9, VMM: yes
        load_backend: loaded CUDA backend from C:\Users\me\.openmono\win\llama.cpp\b9000-cuda-12.4\ggml-cuda.dll
        load_backend: loaded RPC backend from C:\Users\me\.openmono\win\llama.cpp\b9000-cuda-12.4\ggml-rpc.dll
        load_backend: loaded CPU backend from C:\Users\me\.openmono\win\llama.cpp\b9000-cuda-12.4\ggml-cpu-alderlake.dll
        Available devices:
          CUDA0: NVIDIA GeForce RTX 4090 (24563 MiB, 22994 MiB free)
        """;

    private const string NoDevices = "load_backend: loaded CPU backend from ggml-cpu-haswell.dll\nAvailable devices:\n  (none)\n";

    [Fact]
    public void ParsesTheRealListing()
    {
        LlamaDevices.Parse(CudaListing).Should().Equal("CUDA0: NVIDIA GeForce RTX 4090 (24563 MiB, 22994 MiB free)");
        LlamaDevices.FirstFor(Accel.Cuda, CudaListing).Should().StartWith("CUDA0: NVIDIA GeForce RTX 4090");
        LlamaDevices.FirstFor(Accel.Vulkan, CudaListing).Should().BeNull();
        LlamaDevices.Parse(NoDevices).Should().BeEmpty();
        LlamaDevices.Parse("").Should().BeEmpty();
        LlamaDevices.Parse("Available devices:\n  Vulkan0: Intel(R) UHD Graphics (8192 MiB, 7000 MiB free)\n  Vulkan1: NVIDIA GeForce RTX 3060 (12288 MiB, 11000 MiB free)\n")
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task CudaBuildWithoutACudaDeviceIsNotStartedAndVulkanServesTheSameModel()
    {
        using var rig = new LlamaRig();
        rig.Host.Devices = exe => exe.Contains("-cuda-", StringComparison.Ordinal) ? NoDevices : FakeServerHost.DefaultDevices(exe);

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        rig.Host.Launches.Should().ContainSingle();
        result.State!.LlamaBuild.Should().Be("b9000-vulkan");
        result.State.ModelAlias.Should().Be(LlamaRig.Top);
        result.Log.Should().Contain(l => l.StartsWith("WARNING: llama.cpp b9000-cuda-", StringComparison.Ordinal)
            && l.Contains("sees no CUDA device") && l.Contains("cudart64_") && l.EndsWith("Falling back to the next backend.", StringComparison.Ordinal));
        result.Log.Should().Contain("llama.cpp b9000-vulkan sees Vulkan0: NVIDIA GeForce RTX 4090 (24563 MiB, 23012 MiB free).");
    }

    [Fact]
    public async Task NoGpuDeviceInAnyBuildRunsTheCpuRung()
    {
        using var rig = new LlamaRig();
        rig.Host.Devices = _ => NoDevices;

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        rig.Host.Launches.Should().ContainSingle();
        result.State!.LlamaBuild.Should().Be("b9000-cpu");
        result.State.ModelAlias.Should().Be(LlamaRig.Cpu);
        rig.LastLaunch.Value("--n-gpu-layers").Should().BeNull();
        result.Log.Should().Contain(l => l.Contains("sees no Vulkan device"));
    }

    [Fact]
    public async Task ADeviceListThatCannotRunDoesNotBlockTheStart()
    {
        using var rig = new LlamaRig();
        rig.Host.Devices = _ => null;

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        result.State!.LlamaBuild.Should().StartWith("b9000-cuda-");
        result.Log.Should().Contain(l => l.StartsWith("Could not list the devices of llama.cpp b9000-cuda-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheCpuBuildIsNotAskedForDevices()
    {
        using var rig = new LlamaRig(noGpu: true);

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        rig.Host.DeviceQueries.Should().BeEmpty();
    }

    [Fact]
    public void TheRealHostReadsTheListingAndSurvivesAMissingExecutable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omw-devices-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string exe;
            if (OperatingSystem.IsWindows())
            {
                exe = Path.Combine(dir, "llama-server.cmd");
                File.WriteAllText(exe, "@echo off\r\necho Available devices:\r\necho   CUDA0: NVIDIA GeForce RTX 4090 (24563 MiB, 22994 MiB free)\r\n");
            }
            else
            {
                exe = Path.Combine(dir, "llama-server");
                File.WriteAllText(exe, "#!/bin/sh\necho 'Available devices:'\necho \"  CUDA0: NVIDIA GeForce RTX 4090 (24563 MiB, 22994 MiB free) $1\"\n");
                File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            var host = new ServerProcessHost();

            LlamaDevices.FirstFor(Accel.Cuda, host.ListDevices(exe, dir)).Should().StartWith("CUDA0: NVIDIA GeForce RTX 4090");
            host.ListDevices(Path.Combine(dir, "missing-llama-server"), dir).Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TheStartLogNamesTheCudaDevice()
    {
        using var rig = new LlamaRig();

        var result = await rig.Orchestrator().UpAsync(new UpOptions());

        result.Ok.Should().BeTrue(result.Error);
        rig.Host.DeviceQueries.Should().ContainSingle().Which.Should().Contain("-cuda-");
        result.Log.Should().Contain(l => l.StartsWith("llama.cpp b9000-cuda-", StringComparison.Ordinal)
            && l.EndsWith("sees CUDA0: NVIDIA GeForce RTX 4090 (24563 MiB, 23012 MiB free).", StringComparison.Ordinal));
    }
}

public sealed class StopTests
{
    [Fact]
    public async Task ForceNeverStopsAServerOpenMonoDidNotStart()
    {
        using var rig = new LlamaRig();
        rig.Llama.Serve(LlamaRig.Top);
        await rig.Orchestrator().UpAsync(new UpOptions());

        var down = await rig.Orchestrator().DownAsync(force: true);

        down.Stopped.Should().BeFalse();
        rig.Host.Stopped.Should().BeEmpty();
        rig.Llama.Alias.Should().Be(LlamaRig.Top);
    }

    [Fact]
    public async Task StopFindsOurServerWithoutTheStateFile()
    {
        using var rig = new LlamaRig();
        var orchestrator = rig.Orchestrator();
        var up = await orchestrator.UpAsync(new UpOptions());
        orchestrator.Store.Delete();

        var down = await orchestrator.DownAsync(force: false);

        down.Stopped.Should().BeTrue();
        rig.Host.Stopped.Should().Equal(up.State!.ServerPid!.Value);
        rig.Llama.Alias.Should().BeNull();
    }

    [Fact]
    public async Task StopWithNothingRunningIsHarmless()
    {
        using var rig = new LlamaRig();

        var down = await rig.Orchestrator().DownAsync(force: false);

        down.Stopped.Should().BeFalse();
        down.Message.Should().Be("No llama-server started by OpenMono is running.");
        rig.Host.Stopped.Should().BeEmpty();
    }

    [Fact]
    public async Task StopOnARemoteEndpointTouchesNothingLocal()
    {
        using var rig = new LlamaRig();
        rig.WriteSettings("""{"llm":{"endpoint":"http://gpu-box.lan:7474"}}""");
        await rig.Orchestrator().UpAsync(new UpOptions());

        var down = await rig.Orchestrator().DownAsync(force: true);

        down.Stopped.Should().BeFalse();
        rig.Host.Launches.Should().BeEmpty();
        rig.Host.Stopped.Should().BeEmpty();
        rig.Http.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task DownCommandReportsWhatItStopped()
    {
        using var rig = new LlamaRig();
        await rig.Orchestrator().UpAsync(new UpOptions());
        var stdout = new StringWriter();

        var code = await LauncherCli.RunAsync(["inference", "down"], rig.Deps(), stdout, new StringWriter());

        code.Should().Be(0);
        stdout.ToString().Should().Contain("llama-server stopped (pid 4100).");
    }
}

public sealed class VisionFlagTests
{
    [Theory]
    [InlineData(false, LlamaRig.Top, "mmproj-qwen3.8-27B-F16.gguf", 196608, 172032, "q8_0")]
    [InlineData(false, "Qwen3.5-9B-UD-Q4_K_XL", "mmproj-qwen3.5-9B-F16.gguf", 196608, 98304, "q4_0")]
    [InlineData(false, "Qwen3.5-4B-UD-Q4_K_XL", "mmproj-qwen3.5-4B-F16.gguf", 131072, 98304, "q4_0")]
    [InlineData(false, "Qwen3.5-2B-UD-Q4_K_XL", "mmproj-qwen3.5-2B-F16.gguf", 65536, 65536, "q4_0")]
    [InlineData(false, "Qwen3.5-0.8B-UD-Q4_K_XL", "mmproj-qwen3.5-0.8B-F16.gguf", 32768, 32768, "q4_0")]
    [InlineData(true, LlamaRig.Cpu, "mmproj-qwen3.5-2B-F16.gguf", 65536, 65536, "q8_0")]
    public async Task EveryRungGetsItsProjectorAndContext(bool cpuOnly, string alias, string mmproj, int context, int visionContext, string kv)
    {
        using (var rig = new LlamaRig(noGpu: cpuOnly))
        {
            var result = await rig.Orchestrator().UpAsync(new UpOptions(ModelOverride: alias));

            result.Ok.Should().BeTrue(result.Error);
            var args = rig.LastLaunch;
            args.Value("--mmproj").Should().Be(Path.Combine(rig.ModelsDir, mmproj));
            args.Value("--image-min-tokens").Should().Be("1024");
            args.Value("--image-max-tokens").Should().Be("1280");
            args.Value("--ctx-size").Should().Be(visionContext.ToString(System.Globalization.CultureInfo.InvariantCulture));
            args.Value("--cache-type-k").Should().Be(kv);
            args.Value("--cache-type-v").Should().Be(kv);
            result.State!.VisionEnabled.Should().BeTrue();
        }

        using (var rig = new LlamaRig(noGpu: cpuOnly))
        {
            rig.WriteSettings("""{"inference":{"vision_enabled":false}}""");

            var result = await rig.Orchestrator().UpAsync(new UpOptions(ModelOverride: alias));

            result.Ok.Should().BeTrue(result.Error);
            rig.LastLaunch.Arguments.Should().NotContain(["--mmproj", "--image-min-tokens", "--image-max-tokens"]);
            rig.LastLaunch.Value("--ctx-size").Should().Be(context.ToString(System.Globalization.CultureInfo.InvariantCulture));
            result.State!.VisionEnabled.Should().BeFalse();
        }
    }

    [Fact]
    public async Task ExplicitVisionOnWithoutAProjectorIsStillAnError()
    {
        using var rig = new LlamaRig();
        rig.RemoveMmprojs();

        var result = await rig.Orchestrator().UpAsync(new UpOptions(Vision: true));

        result.Ok.Should().BeFalse();
        rig.Orchestrator().Store.Load().Should().BeNull();
    }
}

public sealed class ContainerWiringTests
{
    [Fact]
    public async Task AgentPlanOnAnotherPortPointsTheContainerAtThatPort()
    {
        using var rig = new LlamaRig(port: 7480);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await LauncherCli.RunAsync(
            ["agent-plan", "--workdir", "C:\\proj", "--compose", "c.yml", "--port", "7480", "--no-git-auth"],
            rig.Deps(), stdout, stderr);

        code.Should().Be(0, stderr.ToString());
        using var doc = JsonDocument.Parse(stdout.ToString());
        doc.RootElement.GetProperty("environment").GetProperty("OPENMONO_ENDPOINT").GetString()
            .Should().Be("http://host.docker.internal:7480");
    }

    [Fact]
    public async Task AgentPlanForACpuOnlyMachineCarriesTheCpuModelAndContext()
    {
        using var rig = new LlamaRig(noGpu: true);
        var stdout = new StringWriter();

        var code = await LauncherCli.RunAsync(
            ["agent-plan", "--workdir", "C:\\proj", "--compose", "c.yml", "--no-git-auth"], rig.Deps(), stdout, new StringWriter());

        code.Should().Be(0);
        using var doc = JsonDocument.Parse(stdout.ToString());
        var root = doc.RootElement;
        root.GetProperty("environment").GetProperty("CTX_SIZE").GetString().Should().Be("65536");
        root.GetProperty("docker_args").EnumerateArray().Select(e => e.GetString()).Should().Contain("OPENMONO_MODEL=" + LlamaRig.Cpu);
    }

    [Fact]
    public async Task ApiKeyReachesTheContainerOnlyThroughTheEnvironment()
    {
        using var rig = new LlamaRig();
        rig.Env[LlamaCppConstants.ApiKeyEnvVar] = "s3cret-key";
        var stdout = new StringWriter();

        var code = await LauncherCli.RunAsync(
            ["agent-plan", "--workdir", "C:\\proj", "--compose", "c.yml", "--no-git-auth"], rig.Deps(), stdout, new StringWriter());

        code.Should().Be(0);
        stdout.ToString().Should().NotContain("s3cret-key");
        rig.LastLaunch.Value("--api-key").Should().Be("s3cret-key");
    }

    [Fact]
    public void ComposeAgentServiceResolvesHostDockerInternal()
    {
        var lines = File.ReadAllLines(Path.Combine(RepoPaths.RepoRoot, "docker", "docker-compose.yml"));
        var service = string.Join('\n', lines
            .SkipWhile(l => l != "  agent:")
            .Skip(1)
            .TakeWhile(l => l.Length == 0 || l.StartsWith("    ", StringComparison.Ordinal) || l.TrimStart().StartsWith('#')));

        service.Should().Contain("extra_hosts").And.Contain("\"host.docker.internal:host-gateway\"");
        service.Should().Contain("OPENMONO_API_KEY=${OPENMONO_API_KEY:-${LLAMA_API_KEY:-}}");
    }
}
