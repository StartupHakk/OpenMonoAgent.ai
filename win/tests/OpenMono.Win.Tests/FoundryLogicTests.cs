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

    [Theory]
    [InlineData(24564, true, 65536)]   // RTX 4090 as nvidia-smi reports it
    [InlineData(24576, true, 65536)]   // RTX 3090 / A5000 class
    [InlineData(32607, true, 262144)]  // RTX 5090 as nvidia-smi reports it
    [InlineData(16376, false, 65536)]  // 16 GB class
    public void RealCardSizesLandInTheirClass(long reportedMiB, bool target, int context)
    {
        var selection = ModelSelector.Select(reportedMiB * MiB, "qwen3.8-27b", "qwen3-4b");

        selection.IsTargetModel.Should().Be(target);
        selection.ContextTokens.Should().Be(context);
    }

    [Fact]
    public void TwentyFourGbGetsTargetWithStandardContext()
    {
        var selection = ModelSelector.Select(24 * GiB, "qwen3.8-27b");

        selection.IsTargetModel.Should().BeTrue();
        selection.ModelAlias.Should().Be("qwen3.8-27b");
        selection.ContextTokens.Should().Be(FoundryConstants.StandardContextTokens);
    }

    [Fact]
    public void ThirtyTwoGbUnlocksFullContext()
    {
        var selection = ModelSelector.Select(48 * GiB, "qwen3.8-27b");

        selection.IsTargetModel.Should().BeTrue();
        selection.ContextTokens.Should().Be(FoundryConstants.MaxContextTokens);
    }

    [Fact]
    public void UnderFloorFallsBackWithReason()
    {
        var selection = ModelSelector.Select(12 * GiB, "qwen3.8-27b", "qwen3-4b");

        selection.IsTargetModel.Should().BeFalse();
        selection.ModelAlias.Should().Be("qwen3-4b");
        selection.Reason.Should().Contain("24 GB");
    }

    [Fact]
    public void UnknownVramStillServesTarget()
    {
        var selection = ModelSelector.Select(null, "qwen3.8-27b");

        selection.IsTargetModel.Should().BeTrue();
        selection.ContextTokens.Should().Be(FoundryConstants.StandardContextTokens);
    }

    [Fact]
    public void MissingTargetInCatalogFallsBack()
    {
        var selection = ModelSelector.Select(24 * GiB, null, "qwen3-4b");

        selection.IsTargetModel.Should().BeFalse();
        selection.ModelAlias.Should().Be("qwen3-4b");
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
        handler.Respond("/v1/models", FakeHttpHandler.Json("""{"data":[{"id":"qwen3.8-27b-instruct-cuda-gpu:1"}]}"""));
        var runner = new FakeCliRunner();
        var service = new CliFoundryService(runner, CheckerWith(handler), NoDelay);

        var result = await service.StartOrAdoptAsync("qwen3.8-27b", "http://127.0.0.1:7474");

        result.AdoptedExisting.Should().BeTrue();
        result.StartedByUs.Should().BeFalse();
        result.ServedModelId.Should().Be("qwen3.8-27b-instruct-cuda-gpu:1");
        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ServiceStartsServerBeforeLoadingModel()
    {
        var serving = false;
        var loaded = false;
        var handler = new FakeHttpHandler();
        handler.RespondJson("/v1/models", () => loaded
            ? """{"data":[{"id":"qwen3.8-27b-instruct-cuda-gpu:1"}]}"""
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
        result.ServedModelId.Should().Be("qwen3.8-27b-instruct-cuda-gpu:1");
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
        handler.Respond("127.0.0.1:51234/v1/models", FakeHttpHandler.Json("""{"data":[{"id":"qwen3.8-27b-instruct-cuda-gpu:1"}]}"""));
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
    [InlineData("qwen2.5-vl-7b", "qwen2.5-vl-7b-instruct-cuda-gpu:2")]
    public void ResolvesAliasToServedVariant(string alias, string expected)
    {
        string[] served = ["qwen3.8-27b-instruct-cuda-gpu:1", "qwen2.5-vl-7b-instruct-cuda-gpu:2"];

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
