using System.Net;
using FluentAssertions;
using OpenMono.Win.Foundry;
using OpenMono.Win.Launcher;
using Xunit;

namespace OpenMono.Win.Tests;

// Trimmed real answers from https://foundrylocal.ai/api/models (CUDA filter,
// 2026-11-05): page 1 carries a continuationToken, page 2 ends the listing.
public static class CatalogFixtures
{
    public static string Page1 => Read("foundrylocal-cuda-page1.json");

    public static string Page2Only => Read("foundrylocal-cuda-page2.json");

    // CPU filter, one page: the generic CPU variants (qwen3.5-2b-generic-cpu:3).
    public static string Cpu => Read("foundrylocal-cpu.json");

    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

// Serves the two fixture pages by continuationToken, optionally after a 307,
// and records every request so tests can check the body and the hops.
public sealed class CatalogApiHandler : HttpMessageHandler
{
    private readonly string? _redirectTo;
    private readonly bool _singlePage;

    public CatalogApiHandler(string? redirectTo = null, bool singlePage = false)
    {
        _redirectTo = redirectTo;
        _singlePage = singlePage;
    }

    public List<(Uri Url, string Body)> Requests { get; } = [];

    public bool Offline { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Offline)
        {
            throw new HttpRequestException("network unreachable");
        }

        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.RequestUri!, body));

        if (request.Method != HttpMethod.Post)
        {
            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        if (_redirectTo is not null && request.RequestUri!.Host == "foundrylocal.ai")
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            redirect.Headers.Location = new Uri(_redirectTo);
            return redirect;
        }

        if (body.Contains(FoundryConstants.CpuExecutionProvider, StringComparison.Ordinal))
        {
            return FakeHttpHandler.Json(CatalogFixtures.Cpu);
        }

        if (_singlePage)
        {
            return FakeHttpHandler.Json(CatalogFixtures.Page2Only);
        }

        return FakeHttpHandler.Json(body.Contains("page-2-token", StringComparison.Ordinal)
            ? CatalogFixtures.Page2Only
            : CatalogFixtures.Page1);
    }
}

public sealed class FoundryWebCatalogClientTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "omw-cat-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_cache, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; DirectoryNotFoundException lands here too.
        }
    }

    [Fact]
    public async Task PaginatesWithContinuationTokenAndSendsTheCudaFilter()
    {
        var handler = new CatalogApiHandler();
        var client = new FoundryWebCatalogClient(new HttpClient(handler));

        var result = await client.ListAsync(FoundryCatalogDevice.Cuda);

        result.Ok.Should().BeTrue(result.Error);
        result.Source.Should().Be("https://foundrylocal.ai/api/models");
        result.Models.Select(m => m.Alias).Should().Contain(FoundryModelLadder.AllAliases);
        result.Models.Should().HaveCount(10);
        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Body.Should().Contain("CUDAExecutionProvider").And.Contain("\"pageSize\":50")
            .And.Contain("Foundry Local on Devices").And.NotContain("continuationToken");
        handler.Requests[1].Body.Should().Contain("\"continuationToken\":\"page-2-token\"");
    }

    [Fact]
    public async Task FollowsTheRedirectWithTheSamePost()
    {
        var handler = new CatalogApiHandler(redirectTo: "https://catalog-mirror.example/api/models");
        var client = new FoundryWebCatalogClient(new HttpClient(handler));

        var result = await client.ListAsync(FoundryCatalogDevice.Cuda);

        result.Ok.Should().BeTrue(result.Error);
        handler.Requests.Select(r => r.Url.Host).Should().Equal("foundrylocal.ai", "catalog-mirror.example", "foundrylocal.ai", "catalog-mirror.example");
        handler.Requests.Should().OnlyContain(r => r.Body.Contains("CUDAExecutionProvider"));
    }

    [Fact]
    public async Task RefusesARedirectAwayFromHttps()
    {
        var handler = new CatalogApiHandler(redirectTo: "http://catalog-mirror.example/api/models");
        var client = new FoundryWebCatalogClient(new HttpClient(handler));

        var result = await client.ListAsync(FoundryCatalogDevice.Cuda);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("away from https");
    }

    [Fact]
    public async Task ParsesAliasesAndVariantMetadata()
    {
        var client = new FoundryWebCatalogClient(new HttpClient(new CatalogApiHandler()));

        var models = (await client.ListAsync(FoundryCatalogDevice.Cuda)).Models;

        var vl = models.Single(m => m.Alias == "qwen3.5-9b");
        vl.AcceptsImages.Should().BeTrue();
        vl.Task.Should().Be("vision-language-chat");
        vl.SupportsToolCalling.Should().BeTrue();
        vl.ContextLength.Should().Be(262144);
        vl.VariantId.Should().Be("qwen3.5-9b-cuda-gpu:4");
        vl.ExecutionProvider.Should().Be("CUDAExecutionProvider");
        vl.VramBytes.Should().BeInRange(6L << 30, 8L << 30);

        var coder = models.Single(m => m.Alias == "qwen3.8-27b");
        coder.AcceptsImages.Should().BeFalse();
        coder.Task.Should().Be("coding");
        coder.FileSizeBytes.Should().BeGreaterThan(17L << 30);
    }

    [Fact]
    public async Task CachesBrieflyThenRefetches()
    {
        var now = new DateTimeOffset(2026, 11, 6, 3, 0, 0, TimeSpan.Zero);
        var handler = new CatalogApiHandler();
        var client = new FoundryWebCatalogClient(new HttpClient(handler), cacheDir: _cache, clock: () => now);

        await client.ListAsync(FoundryCatalogDevice.Cuda);
        var cached = await client.ListAsync(FoundryCatalogDevice.Cuda);
        cached.FromCache.Should().BeTrue();
        handler.Requests.Should().HaveCount(2);

        now += TimeSpan.FromHours(2);
        var fresh = await client.ListAsync(FoundryCatalogDevice.Cuda);
        fresh.FromCache.Should().BeFalse();
        handler.Requests.Should().HaveCount(4);
    }

    [Fact]
    public async Task OfflineFallsBackToAStaleCache()
    {
        var now = new DateTimeOffset(2026, 11, 6, 3, 0, 0, TimeSpan.Zero);
        var handler = new CatalogApiHandler();
        var client = new FoundryWebCatalogClient(new HttpClient(handler), cacheDir: _cache, clock: () => now);
        await client.ListAsync(FoundryCatalogDevice.Cuda);

        now += TimeSpan.FromDays(3);
        handler.Offline = true;
        var result = await client.ListAsync(FoundryCatalogDevice.Cuda);

        result.Ok.Should().BeTrue();
        result.FromCache.Should().BeTrue();
        result.Error.Should().Contain("using the cached catalog");
        result.Models.Should().HaveCount(10);
    }

    [Fact]
    public async Task OfflineWithoutCacheIsNotAnException()
    {
        var client = new FoundryWebCatalogClient(new HttpClient(new CatalogApiHandler { Offline = true }));

        var result = await client.ListAsync(FoundryCatalogDevice.Cuda);

        result.Ok.Should().BeFalse();
        result.Models.Should().BeEmpty();
    }

    [Fact]
    public async Task OffDisablesTheClient()
    {
        var handler = new CatalogApiHandler();
        var client = new FoundryWebCatalogClient(new HttpClient(handler), apiUrl: "off");

        (await client.ListAsync(FoundryCatalogDevice.Cuda)).Ok.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(FoundryCatalogDevice.Cpu, "\"cpu\"", "CPUExecutionProvider")]
    [InlineData(FoundryCatalogDevice.WebGpu, "\"gpu\"", "WebGpuExecutionProvider")]
    public void OtherDevicesUseTheirExecutionProvider(FoundryCatalogDevice device, string deviceValue, string provider)
    {
        FoundryWebCatalogClient.BuildRequestBody(device, null).Should().Contain(deviceValue).And.Contain(provider);
    }

    [Fact]
    public void UrlsNeverUseTheWwwHost()
    {
        FoundryWebCatalogClient.DefaultApiUrl.Should().Be("https://foundrylocal.ai/api/models");
        FoundryWebCatalogClient.ModelsPageUrl.Should().Be("https://foundrylocal.ai/models");
    }

    [Fact]
    public void CatalogViewPrefersTheLocalFoundryList()
    {
        var web = new WebCatalogResult(true,
            [new FoundryCatalogModel("qwen3.5-9b", null, "vision-language-chat", ["text", "image"], true, 262144, 7L << 30, 7L << 30, "gpu", "CUDAExecutionProvider")],
            FoundryWebCatalogClient.DefaultApiUrl, null, false, null);

        var view = new FoundryCatalogView(["qwen3.8-27b"], web);

        view.Resolve("qwen3.8-27b").Should().Be("qwen3.8-27b");
        view.Resolve("qwen3.5-9b").Should().BeNull();
        view.Describe("qwen3.5-9b")!.AcceptsImages.Should().BeTrue();
        new FoundryCatalogView([], web).Resolve("qwen3.5-9b").Should().Be("qwen3.5-9b");
    }
}

public sealed class CatalogCommandTests
{
    [Fact]
    public async Task CheckValidatesEveryLadderAliasAndNamesThisMachinesModel()
    {
        using var rig = new FoundryRig(vramGiB: 24, catalog: []);
        rig.Runner.Respond(FoundryRig.Exe, "model list", 1, "", "not installed");
        var deps = rig.Deps() with { Http = new HttpClient(new CatalogApiHandler()) };
        var stdout = new StringWriter();

        var code = await LauncherCli.RunAsync(["catalog", "check", "--strict"], deps, stdout, new StringWriter());

        code.Should().Be(0, stdout.ToString());
        var text = stdout.ToString();
        foreach (var alias in FoundryModelLadder.AllAliases)
        {
            text.Should().MatchRegex($@"\[ ok \]\s+{System.Text.RegularExpressions.Regex.Escape(alias)}\s");
        }

        text.Should().Contain("This machine (NVIDIA GeForce RTX 4090, 24 GB): qwen3.8-27b.");
        text.Should().NotContain("www.");
    }

    [Fact]
    public async Task CpuOnlyCheckNamesTheCpuVariant()
    {
        using var rig = new FoundryRig(noGpu: true, catalog: []);
        rig.Runner.Respond(FoundryRig.Exe, "model list", 1, "", "not installed");
        var handler = new CatalogApiHandler();
        var deps = rig.Deps() with { Http = new HttpClient(handler) };
        var stdout = new StringWriter();

        var code = await LauncherCli.RunAsync(["catalog", "check", "--strict"], deps, stdout, new StringWriter());

        code.Should().Be(0, stdout.ToString());
        stdout.ToString().Should().Contain("This machine (no dedicated NVIDIA GPU, CPU-only): qwen3.5-2b.")
            .And.Contain("CPU variant: qwen3.5-2b-generic-cpu:3");
        handler.Requests.Should().Contain(r => r.Body.Contains("CPUExecutionProvider"));
    }

    [Fact]
    public async Task StrictCheckFailsWhenTheCatalogLacksALadderAlias()
    {
        using var rig = new FoundryRig(vramGiB: 8, catalog: []);
        rig.Runner.Respond(FoundryRig.Exe, "model list", 1, "", "not installed");
        var deps = rig.Deps() with { Http = new HttpClient(new CatalogApiHandler(singlePage: true)) };
        var stdout = new StringWriter();

        var strict = await LauncherCli.RunAsync(["catalog", "check", "--strict"], deps, stdout, new StringWriter());
        var plain = await LauncherCli.RunAsync(["catalog", "check"], deps, new StringWriter(), new StringWriter());

        strict.Should().Be(1);
        plain.Should().Be(0);
        stdout.ToString().Should().Contain("[MISSING] qwen3.8-27b").And.Contain("qwen3.5-4b");
    }

    [Fact]
    public async Task CheckFailsWhenNothingThisMachineMayServeIsInTheCatalog()
    {
        using var rig = new FoundryRig(vramGiB: 24, catalog: []);
        rig.Runner.Respond(FoundryRig.Exe, "model list", 1, "", "not installed");
        var deps = rig.Deps() with { Http = new HttpClient(new CatalogApiHandler(singlePage: true)) };
        var stdout = new StringWriter();

        var code = await LauncherCli.RunAsync(["catalog", "check"], deps, stdout, new StringWriter());

        code.Should().Be(1);
        stdout.ToString().Should().Contain("serves only qwen3.8-27b");
    }

    [Fact]
    public async Task ListsOtherDevices()
    {
        using var rig = new FoundryRig();
        var handler = new CatalogApiHandler();
        var deps = rig.Deps() with { Http = new HttpClient(handler) };
        var stdout = new StringWriter();

        var code = await LauncherCli.RunAsync(["catalog", "--device", "cpu"], deps, stdout, new StringWriter());

        code.Should().Be(0);
        handler.Requests[0].Body.Should().Contain("CPUExecutionProvider");
        stdout.ToString().Should().Contain("qwen3.5-2b-text").And.Contain("[ladder: no GPU (CPU)]");
    }
}

public sealed class LadderTextTests
{
    private static readonly string[] RetiredAliases =
        ["qwen3-14b", "qwen3-8b", "qwen3-4b", "qwen3-1.7b", "qwen3-0.6b", "qwen3-vl", "qwen2.5-vl"];

    private static string WinDir() => RepoPaths.WinDir;

    [Theory]
    [InlineData("scripts/check-cuda.ps1")]
    [InlineData("README.md")]
    [InlineData("docs/VISION.md")]
    public void HostTextNamesTheLockedLadder(string relative)
    {
        var text = File.ReadAllText(Path.Combine(WinDir(), relative));

        text.Should().ContainAll(FoundryModelLadder.AllAliases);
        foreach (var retired in RetiredAliases)
        {
            text.Should().NotContain(retired);
        }
    }

    [Fact]
    public void NoWinFileUsesTheWwwCatalogHost()
    {
        var needle = string.Join(".", "www", "foundrylocal");
        var offenders = Directory.EnumerateFiles(WinDir(), "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && File.ReadAllText(f).Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        offenders.Should().BeEmpty();
    }
}
