using FluentAssertions;
using Xunit;
using OpenMono.Win.Foundry;

namespace OpenMono.Win.Tests;

public sealed class AliasResolverTests
{
    private static readonly string[] Catalog =
        ["Qwen3.8-27B-GGUF", "qwen3_4b", "phi-4-mini"];

    [Fact]
    public void ExactCaseInsensitiveMatchWins()
    {
        FoundryAliasResolver.Resolve("qwen3.8-27b-gguf", Catalog).Should().Be("Qwen3.8-27B-GGUF");
    }

    [Fact]
    public void PunctuationInsensitiveMatch()
    {
        FoundryAliasResolver.Resolve("qwen3-4b", Catalog).Should().Be("qwen3_4b");
    }

    [Fact]
    public void UnknownAliasReturnsNull()
    {
        FoundryAliasResolver.Resolve("qwen9-99b", Catalog).Should().BeNull();
    }

    [Fact]
    public void EmptyDesiredReturnsNull()
    {
        FoundryAliasResolver.Resolve("", Catalog).Should().BeNull();
    }
}

public sealed class ModelSelectorTests
{
    private const long GiB = 1024L * 1024 * 1024;
    private const long MiB = 1024L * 1024;

    private static readonly string[] Catalog =
        ["qwen3.8-27b", "qwen3.5-9b", "qwen3.5-4b", "qwen3.5-2b", "qwen3.5-0.8b", "qwen3-8b", "qwen3-4b"];

    private static string? InCatalog(string alias) => FoundryAliasResolver.Resolve(alias, Catalog);

    private static Func<string, string?> Without(params string[] missing) =>
        alias => missing.Contains(alias) ? null : InCatalog(alias);

    [Theory]
    [InlineData(24564, "qwen3.8-27b", 65536)]   // RTX 4090 as nvidia-smi reports it
    [InlineData(24576, "qwen3.8-27b", 65536)]   // RTX 3090 / A5000 class
    [InlineData(32607, "qwen3.8-27b", 262144)]  // RTX 5090 as nvidia-smi reports it
    [InlineData(16376, "qwen3.5-9b", 65536)]    // 16 GB class
    [InlineData(12282, "qwen3.5-9b", 65536)]    // RTX 3060 / 4070 12 GB
    [InlineData(8188, "qwen3.5-4b", 65536)]     // 8 GB class
    [InlineData(6144, "qwen3.5-2b", 65536)]     // 6 GB class
    [InlineData(4096, "qwen3.5-2b", 65536)]     // 4 GB class
    [InlineData(2048, "qwen3.5-0.8b", 65536)]   // last resort
    public void RealCardSizesLandOnTheirRung(long reportedMiB, string alias, int context)
    {
        var selection = ModelSelector.Select(reportedMiB * MiB, InCatalog)!;

        selection.ModelAlias.Should().Be(alias);
        selection.IsTargetModel.Should().Be(alias == FoundryConstants.TargetModelAlias);
        selection.ContextTokens.Should().Be(context);
    }

    [Fact]
    public void LadderIsTheLockedQwenLadder()
    {
        FoundryModelLadder.AllAliases.Should().Equal("qwen3.8-27b", "qwen3.5-9b", "qwen3.5-4b", "qwen3.5-2b", "qwen3.5-0.8b");
        FoundryModelLadder.AllAliases.Should().NotContain(a => a.StartsWith("qwen3-", StringComparison.Ordinal));
    }

    [Fact]
    public void UnderFloorSaysWhy()
    {
        var selection = ModelSelector.Select(12 * GiB, InCatalog)!;

        selection.Reason.Should().Contain("below the 24 GB floor").And.Contain("qwen3.5-9b").And.Contain("text and images");
    }

    [Fact]
    public void NoGpuServesQwen35TwoBOnTheCpu()
    {
        var selection = ModelSelector.Select(null, InCatalog)!;

        selection.ModelAlias.Should().Be("qwen3.5-2b");
        selection.CpuOnly.Should().BeTrue();
        selection.IsTargetModel.Should().BeFalse();
        selection.ContextTokens.Should().Be(FoundryConstants.StandardContextTokens);
        selection.Reason.Should().Contain("No dedicated NVIDIA GPU").And.Contain("not tried without a GPU");
        ModelSelector.RungFor(null).Should().BeSameAs(FoundryModelLadder.Cpu);
    }

    [Fact]
    public void NoGpuNeverAsksForTheLargerModels()
    {
        var asked = new List<string>();
        Func<string, string?> recording(Func<string, string?> inner) => alias =>
        {
            asked.Add(alias);
            return inner(alias);
        };

        ModelSelector.Select(null, recording(InCatalog))!.ModelAlias.Should().Be("qwen3.5-2b");
        asked.Should().Equal("qwen3.5-2b");

        asked.Clear();
        ModelSelector.Select(null, recording(Without("qwen3.5-2b")))!.ModelAlias.Should().Be("qwen3.5-0.8b");
        asked.Should().Equal("qwen3.5-2b", "qwen3.5-0.8b");

        asked.Clear();
        ModelSelector.Select(null, recording(Without("qwen3.5-2b", "qwen3.5-0.8b"))).Should().BeNull();
        asked.Should().Equal("qwen3.5-2b", "qwen3.5-0.8b");
    }

    [Fact]
    public void TwentyFourGbNeverStepsDown()
    {
        ModelSelector.Select(24 * GiB, Without("qwen3.8-27b")).Should().BeNull();
        ModelSelector.Select(48 * GiB, Without("qwen3.8-27b")).Should().BeNull();
    }

    [Fact]
    public void SmallerCardsStepDownTheQwen35LadderOnly()
    {
        var selection = ModelSelector.Select(12 * GiB, Without("qwen3.5-9b"))!;

        selection.ModelAlias.Should().Be("qwen3.5-4b");
        selection.SkippedAliases.Should().Equal("qwen3.5-9b");
        selection.Reason.Should().Contain("Not in the Foundry catalog: qwen3.5-9b");
    }

    [Fact]
    public void SmallerCardsNeverStepUp()
    {
        ModelSelector.Select(8 * GiB, Without("qwen3.5-4b", "qwen3.5-2b", "qwen3.5-0.8b")).Should().BeNull();
    }

    [Fact]
    public void ResolvedCatalogSpellingIsServed()
    {
        ModelSelector.Select(8 * GiB, alias => alias == "qwen3.5-4b" ? "Qwen3.5-4B" : null)!
            .ModelAlias.Should().Be("Qwen3.5-4B");
    }
}

public sealed class OmaContractTests
{
    [Theory]
    [InlineData("http://127.0.0.1:7474", "http://127.0.0.1:7474")]
    [InlineData("http://127.0.0.1:7474/", "http://127.0.0.1:7474")]
    [InlineData("http://127.0.0.1:7474/v1", "http://127.0.0.1:7474")]
    [InlineData("http://127.0.0.1:7474/v1/", "http://127.0.0.1:7474")]
    public void TrimRootNormalizesEndpointSpellings(string input, string expected)
    {
        OmaEndpointContract.TrimRoot(input).Should().Be(expected);
    }

    [Fact]
    public void UrlBuildersAppendPaths()
    {
        OmaEndpointContract.ChatCompletionsUrl("http://127.0.0.1:7474/v1")
            .Should().Be("http://127.0.0.1:7474/v1/chat/completions");
        OmaEndpointContract.ModelsUrl(FoundryConstants.DefaultLoopbackEndpoint)
            .Should().Be("http://127.0.0.1:7474/v1/models");
        OmaEndpointContract.HealthUrl(FoundryConstants.DefaultLoopbackEndpoint)
            .Should().Be("http://127.0.0.1:7474/health");
    }

    [Fact]
    public void ForFoundryPointsAtLoopback()
    {
        var (endpoint, model) = OmaEndpointContract.ForFoundry("qwen3.8-27b");

        endpoint.Should().Be("http://127.0.0.1:7474");
        model.Should().Be("qwen3.8-27b");
    }
}

public sealed class HealthAndDeciderTests
{
    private static FoundryHealthChecker CheckerWith(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) });

    [Fact]
    public async Task ListsModelsFromV1()
    {
        var handler = new FakeHttpHandler();
        handler.Respond("/v1/models", FakeHttpHandler.Json("""{"data":[{"id":"qwen3.8-27b"}]}"""));
        handler.Respond("/health", new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

        var health = await CheckerWith(handler).CheckAsync("http://127.0.0.1:7474");

        health.Reachable.Should().BeTrue();
        health.ListsModel("QWEN3.8-27b").Should().BeTrue();
        health.Error.Should().BeNull();
    }

    [Fact]
    public async Task EmptyModelListIsReachableButServesNothing()
    {
        var handler = new FakeHttpHandler();
        handler.Respond("/v1/models", FakeHttpHandler.Json("""{"data":[]}"""));

        var health = await CheckerWith(handler).CheckAsync("http://127.0.0.1:7474");

        health.Reachable.Should().BeTrue();
        health.Models.Should().BeEmpty();
        health.ListsModel("qwen3.8-27b").Should().BeFalse();
    }

    [Fact]
    public async Task UnreachableServerIsNotAnException()
    {
        var handler = new FakeHttpHandler();

        var health = await CheckerWith(handler).CheckAsync("http://127.0.0.1:7474");

        health.Reachable.Should().BeFalse();
        health.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ReachableWithModelAdopts()
    {
        var health = new FoundryHealth(true, ["qwen3.8-27b"], false, null);

        var plan = FoundryEndpointDecider.Decide(health, "http://127.0.0.1:7474", "qwen3.8-27b");

        plan.Decision.Should().Be(EndpointDecision.AdoptExisting);
    }

    [Fact]
    public void UnreachableStartsNew()
    {
        var health = new FoundryHealth(false, [], false, "connection refused");

        var plan = FoundryEndpointDecider.Decide(health, "http://127.0.0.1:7474", "qwen3.8-27b");

        plan.Decision.Should().Be(EndpointDecision.StartNew);
        plan.Reason.Should().Contain("connection refused");
    }

    private static readonly Func<TimeSpan, CancellationToken, Task> NoDelay = (_, _) => Task.CompletedTask;

    [Fact]
    public async Task ServiceAdoptsHealthyServerWithoutSpawning()
    {
        var handler = new FakeHttpHandler();
        handler.Respond("/v1/models", FakeHttpHandler.Json("""{"data":[{"id":"qwen3.8-27b-cuda-gpu:1"}]}"""));
        var runner = new FakeCliRunner();
        var service = new CliFoundryService(runner, CheckerWith(handler), NoDelay);

        var result = await service.StartOrAdoptAsync("qwen3.8-27b", "http://127.0.0.1:7474");

        result.AdoptedExisting.Should().BeTrue();
        result.StartedByUs.Should().BeFalse();
        result.ServedModelId.Should().Be("qwen3.8-27b-cuda-gpu:1");
        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ServiceReplacesAForeignModelOnAnAdoptedDaemon()
    {
        const string deepSeek = "DeepSeek-R1-Distill-Qwen-1.5B-openvino-gpu:2";
        var served = new List<string> { deepSeek };
        var handler = new FakeHttpHandler();
        handler.RespondJson("/v1/models", () => System.Text.Json.JsonSerializer.Serialize(new { data = served.Select(id => new { id }) }));
        var runner = new FakeCliRunner();
        var exe = FoundryCliDetector.CliExecutableName;
        runner.Respond(exe, "model load qwen3.5-2b-generic-cpu:3", 0, "loaded\n");
        runner.OnCall(exe, "model load qwen3.5-2b-generic-cpu:3", () => served.Add("qwen3.5-2b-generic-cpu:3"));
        runner.Respond(exe, $"model unload {deepSeek}", 0, "unloaded\n");
        runner.OnCall(exe, $"model unload {deepSeek}", () => served.Remove(deepSeek));
        var service = new CliFoundryService(runner, CheckerWith(handler), NoDelay);

        var result = await service.StartOrAdoptAsync("qwen3.5-2b", "http://127.0.0.1:7474", loadTarget: "qwen3.5-2b-generic-cpu:3");

        result.AdoptedExisting.Should().BeTrue();
        result.ServedModelId.Should().Be("qwen3.5-2b-generic-cpu:3");
        result.ReplacedModels.Should().Equal(deepSeek);
        result.ForeignModels.Should().BeEmpty();
        runner.Calls.Select(c => c.Arguments).Should().Equal("model load qwen3.5-2b-generic-cpu:3", $"model unload {deepSeek}");
        served.Should().Equal("qwen3.5-2b-generic-cpu:3");
    }

    [Fact]
    public async Task ServiceLeavesOtherModelsAloneWhenTheLadderModelIsAlreadyServedWithoutThem()
    {
        var handler = new FakeHttpHandler();
        handler.Respond("/v1/models", FakeHttpHandler.Json("""{"data":[{"id":"qwen3.5-2b-generic-cpu:3"}]}"""));
        var runner = new FakeCliRunner();
        var service = new CliFoundryService(runner, CheckerWith(handler), NoDelay);

        var result = await service.StartOrAdoptAsync("qwen3.5-2b", "http://127.0.0.1:7474", loadTarget: "qwen3.5-2b-generic-cpu:3");

        result.ServedModelId.Should().Be("qwen3.5-2b-generic-cpu:3");
        result.ReplacedModels.Should().BeEmpty();
        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ServiceStartsServerBeforeLoadingModel()
    {
        var serving = false;
        var loaded = false;
        var handler = new FakeHttpHandler();
        handler.RespondJson("/v1/models", () => loaded
            ? """{"data":[{"id":"qwen3.8-27b-cuda-gpu:1"}]}"""
            : """{"data":[]}""");
        var gated = new GatedHandler(handler, () => serving);
        var runner = new FakeCliRunner();
        var exe = FoundryCliDetector.CliExecutableName;
        runner.Respond(exe, "server status", 1, "", "not running");
        runner.Respond(exe, "server start --port 7474 --idle-timeout 0", 0, "started\n");
        runner.OnCall(exe, "server start --port 7474 --idle-timeout 0", () => serving = true);
        runner.Respond(exe, "model load qwen3.8-27b", 0, "loaded\n");
        runner.OnCall(exe, "model load qwen3.8-27b", () => loaded = true);
        var service = new CliFoundryService(runner, CheckerWith(gated), NoDelay);

        var result = await service.StartOrAdoptAsync("qwen3.8-27b", "http://127.0.0.1:7474");

        result.AdoptedExisting.Should().BeFalse();
        result.StartedByUs.Should().BeTrue();
        result.Healthy.Should().BeTrue();
        result.ServedModelId.Should().Be("qwen3.8-27b-cuda-gpu:1");
        runner.Calls.Select(c => c.Arguments).Should().ContainInOrder(
            "server start --port 7474 --idle-timeout 0",
            "model load qwen3.8-27b");
    }

    [Fact]
    public async Task ServiceAdoptsUserDaemonOnAnotherPort()
    {
        var handler = new FakeHttpHandler();
        handler.Respond("127.0.0.1:7474/v1/models", new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        handler.Respond("127.0.0.1:7474/health", new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        handler.Respond("127.0.0.1:51234/v1/models", FakeHttpHandler.Json("""{"data":[{"id":"qwen3.8-27b-cuda-gpu:1"}]}"""));
        var runner = new FakeCliRunner();
        var exe = FoundryCliDetector.CliExecutableName;
        runner.Respond(exe, "server status", 0, "Status: Running\nLocal Endpoint: http://127.0.0.1:51234/v1\n");
        var service = new CliFoundryService(runner, CheckerWith(handler), NoDelay);

        var result = await service.StartOrAdoptAsync("qwen3.8-27b", "http://127.0.0.1:7474");

        result.AdoptedExisting.Should().BeTrue();
        result.StartedByUs.Should().BeFalse();
        result.Endpoint.Should().Be("http://127.0.0.1:51234");
        runner.Calls.Should().NotContain(c => c.Arguments.StartsWith("server start"));
    }

    [Fact]
    public async Task ServiceReportsUnhealthyWithoutLoading()
    {
        var runner = new FakeCliRunner();
        var exe = FoundryCliDetector.CliExecutableName;
        runner.Respond(exe, "server status", 1, "", "not running");
        runner.Respond(exe, "server start --port 7474 --idle-timeout 0", 1, "", "cuda init failed");
        var service = new CliFoundryService(runner, CheckerWith(new FakeHttpHandler()), NoDelay, healthAttempts: 2);

        var result = await service.StartOrAdoptAsync("qwen3.8-27b", "http://127.0.0.1:7474");

        result.Healthy.Should().BeFalse();
        result.StartedByUs.Should().BeFalse();
        result.Log.Should().Contain(l => l.Contains("cuda init failed"));
        runner.Calls.Should().NotContain(c => c.Arguments.StartsWith("model load"));
    }

    [Fact]
    public void CommandShapesArePinned()
    {
        var exe = FoundryCliDetector.CliExecutableName;
        FoundryCliCommands.ModelLoad("qwen3.8-27b").Should().Be((exe, "model load qwen3.8-27b"));
        FoundryCliCommands.ServerStart(7474).Should().Be((exe, "server start --port 7474 --idle-timeout 0"));
        FoundryCliCommands.ServerStop().Should().Be((exe, "server stop"));
        FoundryCliCommands.ServerStatus().Should().Be((exe, "server status"));
        FoundryCliCommands.ModelListJson().Should().Be((exe, "model list --output json"));
    }
}

// Lets a test model "server down" (every request refused) until a flag flips.
public sealed class GatedHandler : DelegatingHandler
{
    private readonly Func<bool> _open;

    public GatedHandler(HttpMessageHandler inner, Func<bool> open)
        : base(inner)
    {
        _open = open;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        _open()
            ? base.SendAsync(request, cancellationToken)
            : throw new HttpRequestException("connection refused");
}

public sealed class ServedModelIdTests
{
    [Theory]
    [InlineData("qwen3.8-27b", "qwen3.8-27b-instruct-cuda-gpu:1")]
    [InlineData("Qwen3.8_27B", "qwen3.8-27b-instruct-cuda-gpu:1")]
    [InlineData("qwen3.5-9b", "qwen3.5-9b-cuda-gpu:4")]
    public void ResolvesAliasToServedVariant(string alias, string expected)
    {
        string[] served = ["qwen3.8-27b-instruct-cuda-gpu:1", "qwen3.5-9b-cuda-gpu:4"];

        FoundryModelIdResolver.ResolveServedId(alias, served).Should().Be(expected);
    }

    [Fact]
    public void ExactMatchWins()
    {
        FoundryModelIdResolver.ResolveServedId("qwen3-4b", ["qwen3-4b-generic-cpu:1", "qwen3-4b"]).Should().Be("qwen3-4b");
    }

    [Fact]
    public void ShortAliasDoesNotCaptureLongerModel()
    {
        FoundryModelIdResolver.ResolveServedId("qwen3", ["qwen3.8-27b-instruct-cuda-gpu:1"]).Should().BeNull();
        FoundryModelIdResolver.ResolveServedId("phi-4", ["phi-4-mini-instruct-cuda-gpu:1"]).Should().BeNull();
        FoundryModelIdResolver.ResolveServedId("qwen3-4b", ["qwen3-40b-instruct-cuda-gpu:1"]).Should().BeNull();
        FoundryModelIdResolver.ResolveServedId("qwen3.8-2", ["qwen3.8-27b-instruct-cuda-gpu:1"]).Should().BeNull();
    }

    [Fact]
    public void PrefersGpuVariant()
    {
        FoundryModelIdResolver.ResolveServedId("phi-4-mini", ["phi-4-mini-generic-cpu:1", "phi-4-mini-instruct-cuda-gpu:1"])
            .Should().Be("phi-4-mini-instruct-cuda-gpu:1");
    }

    [Fact]
    public void NothingServedIsNull()
    {
        FoundryModelIdResolver.ResolveServedId("qwen3.8-27b", []).Should().BeNull();
    }
}
