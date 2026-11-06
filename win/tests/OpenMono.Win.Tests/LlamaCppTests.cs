using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Win.Inference;
using Xunit;

namespace OpenMono.Win.Tests;

public sealed class LlamaCppReleaseTests
{
    private static IReadOnlyList<LlamaRelease> Releases(params string[] tags) =>
        LlamaCppRelease.ParseReleases(LlamaRig.ReleasesJson(tags));

    [Theory]
    [InlineData("| NVIDIA-SMI 572.16   Driver Version: 572.16   CUDA Version: 12.8 |", "12.8")]
    [InlineData("CUDA Version: 13.0", "13.0")]
    [InlineData("NVIDIA-SMI has failed", null)]
    public void ReadsTheDriverCudaVersion(string banner, string? expected)
    {
        LlamaCppRelease.ParseDriverCudaVersion(banner)?.ToString().Should().Be(expected);
    }

    [Theory]
    [InlineData("12.8", "cuda-12.4")]
    [InlineData("12.4", "cuda-12.4")]
    [InlineData("13.5", "cuda-13.5")]
    [InlineData("14.0", "cuda-13.5")]
    public void CudaTakesTheNewestBuildTheDriverRuns(string driver, string flavor)
    {
        var choice = LlamaCppRelease.Pick(Releases("b9000"), Accel.Cuda, Version.Parse(driver))!;

        choice.Flavor.Should().Be(flavor);
        choice.Server.Name.Should().Be($"llama-b9000-bin-win-{flavor}-x64.zip");
        choice.Cudart!.Name.Should().Be($"cudart-llama-bin-win-{flavor}-x64.zip");
    }

    [Fact]
    public void DriverOlderThanEveryCudaBuildGetsNone()
    {
        LlamaCppRelease.Pick(Releases("b9000"), Accel.Cuda, new Version(11, 8)).Should().BeNull();
        LlamaCppRelease.Pick(Releases("b9000"), Accel.Cuda, null).Should().BeNull();
    }

    [Fact]
    public void SkipsTagsWithoutWindowsBuildsAndTakesTheNewestBuild()
    {
        var choice = LlamaCppRelease.Pick(Releases("b8999", "b9001", "b9000"), Accel.Cpu, null)!;

        choice.Tag.Should().Be("b9001");
        choice.Id.Should().Be("b9001-cpu");
        LlamaCppRelease.Pick(Releases("b9000"), Accel.Vulkan, null)!.Server.Name.Should().Be("llama-b9000-bin-win-vulkan-x64.zip");
    }

    [Fact]
    public void PinnedTagIsHonored()
    {
        LlamaCppRelease.Pick(Releases("b9001", "b9000"), Accel.Cpu, null, pinnedTag: "b9000")!.Tag.Should().Be("b9000");
        LlamaCppRelease.Pick(Releases("b9001"), Accel.Cpu, null, pinnedTag: "b1").Should().BeNull();
    }

    [Fact]
    public void BadJsonIsNoReleases()
    {
        LlamaCppRelease.ParseReleases("<html>rate limited</html>").Should().BeEmpty();
    }
}

public sealed class ModelLadderTests
{
    private static readonly string Catalog = Path.Combine(RepoPaths.RepoRoot, "scripts", "models.json");

    [Fact]
    public void WindowsLadderKeepsTheSizeRatiosOfThePreviousLadder()
    {
        var ladder = ModelLadder.Load(Catalog);

        ladder.Gpu.Select(r => (r.Key, r.MinVramGiB, r.Alias)).Should().Equal(
            ("24", 23.0, "Qwen3.8-27B-UD-Q4_K_M"),
            ("12", 11.0, "Qwen3.5-9B-UD-Q4_K_XL"),
            ("8", 7.0, "Qwen3.5-4B-UD-Q4_K_XL"),
            ("4", 3.0, "Qwen3.5-2B-UD-Q4_K_XL"),
            ("0", 0.0, "Qwen3.5-0.8B-UD-Q4_K_XL"));
        ladder.Cpu.Alias.Should().Be("Qwen3.5-2B-UD-Q4_K_XL");
        ladder.Cpu.Kv.Should().Be("q8_0");
    }

    [Fact]
    public void TopTierIsTheLinuxTopTierGguf()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Catalog));
        var qwen = doc.RootElement.GetProperty("models").EnumerateArray().First(m => m.GetProperty("id").GetString() == "qwen");
        var linux = qwen.GetProperty("platforms").GetProperty("linux").GetProperty("tiers").GetProperty("24");
        var top = ModelLadder.Load(Catalog).Gpu[0];

        top.File.Should().Be(linux.GetProperty("name").GetString());
        top.Url.Should().Be(linux.GetProperty("url").GetString());
        top.Mmproj.Should().Be(linux.GetProperty("mmproj").GetString());
        top.Context.Should().Be(linux.GetProperty("ctx").GetInt32());
        top.Kv.Should().Be(linux.GetProperty("kv").GetString());
        linux.GetProperty("name").GetString().Should().Be("Qwen3.8-27B-UD-Q4_K_M.gguf");
    }

    [Fact]
    public void EveryRungIsAGgufFromHuggingFace()
    {
        foreach (var rung in ModelLadder.Load(Catalog).All)
        {
            rung.File.Should().EndWith(".gguf");
            rung.Url.Should().StartWith("https://huggingface.co/unsloth/").And.EndWith("/" + rung.File);
            rung.Mmproj.Should().EndWith("-F16.gguf");
            rung.MmprojUrl.Should().EndWith("/mmproj-F16.gguf");
            rung.VisionContext.Should().BeLessThanOrEqualTo(rung.Context);
        }
    }

    [Theory]
    [InlineData(null, "cpu")]
    [InlineData(24564L * 1024 * 1024, "24")]
    [InlineData(16376L * 1024 * 1024, "12")]
    [InlineData(12282L * 1024 * 1024, "12")]
    [InlineData(8188L * 1024 * 1024, "8")]
    [InlineData(4096L * 1024 * 1024, "4")]
    [InlineData(2048L * 1024 * 1024, "0")]
    public void PicksByDedicatedVram(long? vramBytes, string key)
    {
        ModelLadder.Load(Catalog).Pick(vramBytes).Key.Should().Be(key);
    }

    [Fact]
    public void FindsByAliasOrFileName()
    {
        var ladder = ModelLadder.Load(Catalog);

        ladder.Find("qwen3.5-4b-ud-q4_k_xl")!.Key.Should().Be("8");
        ladder.Find("Qwen3.5-4B-UD-Q4_K_XL.gguf")!.Key.Should().Be("8");
        ladder.Find("qwen3.5-4b").Should().BeNull();
    }

    [Fact]
    public void CatalogWithoutWindowsTiersIsRejected()
    {
        var act = () => ModelLadder.Parse("""{"schema":1,"default":"qwen","models":[{"id":"qwen","platforms":{"linux":{"tiers":{}}}}]}""");

        act.Should().Throw<InvalidDataException>().WithMessage("*platforms.windows.tiers*");
    }

    [Fact]
    public void LocatesTheCatalogFromTheRepoOrAboveTheBinary()
    {
        ModelLadder.Locate(_ => RepoPaths.RepoRoot).Should().Be(Catalog);
        ModelLadder.Locate(_ => null, AppContext.BaseDirectory).Should().Be(Path.GetFullPath(Catalog));
    }
}

public sealed class LlamaServerArgsTests
{
    private static LlamaServerConfig Config(Accel accel, string? mmproj = null, string? key = null) => new(
        "C:\\Users\\me\\open mono\\models\\m.gguf", mmproj, "m", 7474, 65536, "q4_0", accel, 8, "C:\\log.txt", key);

    [Fact]
    public void GpuFlagsMatchTheLinuxOverride()
    {
        var args = LlamaServerArgs.Build(Config(Accel.Cuda, "C:\\m\\mmproj.gguf"));

        args.Should().ContainInOrder("--model", "C:\\Users\\me\\open mono\\models\\m.gguf", "--alias", "m", "--host", "127.0.0.1", "--port", "7474");
        args.Should().ContainInOrder("--n-gpu-layers", "99", "--flash-attn", "on", "--cache-type-k", "q4_0", "--cache-type-v", "q4_0",
            "--batch-size", "2048", "--ubatch-size", "1024", "--parallel", "1", "--jinja", "--reasoning", "off", "--metrics");
        args.Should().ContainInOrder("--mmproj", "C:\\m\\mmproj.gguf", "--image-min-tokens", "1024", "--image-max-tokens", "1280");
        args.Should().NotContain("--api-key");
    }

    [Fact]
    public void CpuFlagsMatchTheLinuxCpuOverride()
    {
        var args = LlamaServerArgs.Build(Config(Accel.Cpu));

        args.Should().ContainInOrder("--threads", "8", "--threads-batch", "8");
        args.Should().NotContain("--n-gpu-layers").And.NotContain("--mmproj");
    }

    [Fact]
    public void FingerprintTracksBuildAndFlagsButNeverTheKey()
    {
        var a = LlamaServerArgs.Fingerprint("b1-cpu", Config(Accel.Cpu, key: "k1"));

        a.Should().NotContain("k1");
        LlamaServerArgs.Fingerprint("b1-cpu", Config(Accel.Cpu, key: "k2")).Should().Be(a);
        LlamaServerArgs.Fingerprint("b2-cpu", Config(Accel.Cpu, key: "k1")).Should().NotBe(a);
        LlamaServerArgs.Fingerprint("b1-cpu", Config(Accel.Cpu)).Should().NotBe(a);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("C:\\open mono\\m.gguf", "\"C:\\open mono\\m.gguf\"")]
    [InlineData("C:\\dir with space\\", "\"C:\\dir with space\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("", "\"\"")]
    public void QuotesForTheWindowsCommandLine(string arg, string quoted)
    {
        LlamaServerArgs.Quote(arg).Should().Be(quoted);
    }

    [Fact]
    public void ThreadsFollowLlamaThreads()
    {
        LlamaServerArgs.DefaultThreads(k => k == "LLAMA_THREADS" ? "6" : null, Accel.Cuda).Should().Be(6);
        LlamaServerArgs.DefaultThreads(_ => null, Accel.Cuda).Should().BeInRange(1, 14);
    }
}

public sealed class ServerHealthCheckerTests
{
    [Fact]
    public async Task LoadingIsReachableButNotReady()
    {
        var http = new FakeHttpHandler();
        http.Respond("/health", FakeHttpHandler.Json("""{"error":{"message":"Loading model"}}""", HttpStatusCode.ServiceUnavailable));
        http.Respond("/v1/models", FakeHttpHandler.Json("""{"error":"loading"}""", HttpStatusCode.ServiceUnavailable));

        var health = await new ServerHealthChecker(new HttpClient(http)).CheckAsync("http://127.0.0.1:7474");

        health.Should().BeEquivalentTo(new { Reachable = true, Ready = false, Loading = true });
    }

    [Fact]
    public async Task ReadyServerListsItsAlias()
    {
        var http = new FakeHttpHandler();
        new FakeLlamaServer(http).Serve("Qwen3.5-9B-UD-Q4_K_XL");

        var health = await new ServerHealthChecker(new HttpClient(http)).CheckAsync("http://127.0.0.1:7474/v1");

        health.Ready.Should().BeTrue();
        health.ListsModel("qwen3.5-9b-ud-q4_k_xl").Should().BeTrue();
        health.ListsModel("Qwen3.5-9B-UD-Q4_K_XL.gguf").Should().BeTrue();
    }

    [Fact]
    public async Task RefusedIsUnreachable()
    {
        var health = await new ServerHealthChecker(new HttpClient(new UnreachableHandler())).CheckAsync("http://127.0.0.1:7474");

        health.Reachable.Should().BeFalse();
        health.Error.Should().Contain("unreachable");
    }
}

// Serves one body, honoring Range like Hugging Face's CDN; can cut the first
// response short to model a dropped connection.
public sealed class RangeHandler : HttpMessageHandler
{
    private readonly byte[] _body;

    public RangeHandler(byte[] body)
    {
        _body = body;
    }

    public int CutFirstAt { get; set; } = -1;

    public List<long?> RangeStarts { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var from = request.Headers.Range?.Ranges.First().From;
        RangeStarts.Add(from);
        var start = (int)(from ?? 0);
        var slice = _body[start..];
        var claimed = slice.Length;
        if (CutFirstAt >= 0 && RangeStarts.Count == 1)
        {
            slice = slice[..CutFirstAt];
        }

        var response = new HttpResponseMessage(from is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(slice),
        };
        response.Content.Headers.ContentLength = claimed;
        if (from is not null)
        {
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, _body.Length - 1, _body.Length);
        }

        return Task.FromResult(response);
    }
}

public sealed class DownloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "omw-dl-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task InterruptedDownloadResumesWithRange()
    {
        var body = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        var handler = new RangeHandler(body) { CutFirstAt = 1200 };
        var downloader = new FileDownloader(new HttpClient(handler));
        var target = Path.Combine(_dir, "m.gguf");

        var first = await downloader.DownloadAsync("http://hf.test/m.gguf", target);

        first.Ok.Should().BeFalse();
        first.Error.Should().Contain("run the same command again to resume");
        File.Exists(target).Should().BeFalse();

        var second = await downloader.DownloadAsync("http://hf.test/m.gguf", target);

        second.Ok.Should().BeTrue(second.Error);
        handler.RangeStarts.Should().Equal(null, 1200);
        File.ReadAllBytes(target).Should().Equal(body);
        File.Exists(target + ".part").Should().BeFalse();
    }

    [Fact]
    public async Task ErrorPageIsRejected()
    {
        var http = new FakeHttpHandler();
        http.Respond("/m.gguf", FakeHttpHandler.Json("""{"error":"not found"}"""));

        var result = await new FileDownloader(new HttpClient(http)).DownloadAsync("http://hf.test/m.gguf", Path.Combine(_dir, "m.gguf"), minBytes: 1024);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("likely an HTTP error page");
        File.Exists(Path.Combine(_dir, "m.gguf")).Should().BeFalse();
    }

    [Fact]
    public async Task MirrorServesModelAndProjectorLikeInstallSh()
    {
        var http = new FakeHttpHandler();
        http.RespondBytes("mirror.lan:8080/models/Qwen3.5-2B-UD-Q4_K_XL.gguf", () => new byte[ModelStore.MinModelBytes]);
        http.RespondBytes("mirror.lan:8080/models/mmproj-qwen3.5-2B-F16.gguf", () => new byte[ModelStore.MinMmprojBytes]);
        var rung = ModelLadder.Load(Path.Combine(RepoPaths.RepoRoot, "scripts", "models.json")).Cpu;
        var store = new ModelStore(new HttpClient(http), _dir, k => k == "OPENMONO_MODEL_MIRROR" ? "http://mirror.lan:8080/" : null);

        var files = await store.EnsureAsync(rung, withMmproj: true);

        files.Error.Should().BeNull();
        files.MmprojPath.Should().Be(Path.Combine(_dir, "mmproj-qwen3.5-2B-F16.gguf"));
        http.Requests.Should().Equal(
            "GET mirror.lan:8080/models/Qwen3.5-2B-UD-Q4_K_XL.gguf",
            "GET mirror.lan:8080/models/mmproj-qwen3.5-2B-F16.gguf");

        http.Requests.Clear();
        await store.EnsureAsync(rung, withMmproj: true);
        http.Requests.Should().BeEmpty();
    }

    [Fact]
    public void ModelsLiveInTheRepoModelsDirectoryLikeOnLinux()
    {
        var catalog = Path.Combine(RepoPaths.RepoRoot, "scripts", "models.json");

        ModelStore.ModelsDirFor(catalog, _ => null).Should().Be(Path.Combine(RepoPaths.RepoRoot, "models"));
        ModelStore.ModelsDirFor(catalog, k => k == "OPENMONO_MODELS_DIR" ? "D:\\gguf" : null).Should().Be("D:\\gguf");
    }
}
