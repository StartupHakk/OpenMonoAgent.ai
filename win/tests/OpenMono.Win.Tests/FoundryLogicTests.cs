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
    private static FoundryHealthChecker CheckerWith(FakeHttpHandler handler) =>
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

    [Fact]
    public async Task ServiceAdoptsHealthyServerWithoutSpawning()
    {
        var handler = new FakeHttpHandler();
        handler.Respond("/v1/models", FakeHttpHandler.Json("""{"data":[{"id":"qwen3.8-27b"}]}"""));
        var runner = new FakeCliRunner();
        var service = new CliFoundryService(runner, CheckerWith(handler));

        var result = await service.StartOrAdoptAsync("qwen3.8-27b", "http://127.0.0.1:7474");

        result.AdoptedExisting.Should().BeTrue();
        runner.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ServiceStartsWhenNothingServes()
    {
        var handler = new FakeHttpHandler(); // serves nothing: every path 404s.
        var runner = new FakeCliRunner();
        var exe = FoundryCliDetector.CliExecutableName;
        runner.Respond(exe, "model load qwen3.8-27b", 0, "loaded\n");
        runner.Respond(exe, "server start --port 7474", 0, "serving\n");
        var service = new CliFoundryService(runner, CheckerWith(handler));

        var result = await service.StartOrAdoptAsync("qwen3.8-27b", "http://127.0.0.1:7474");

        result.AdoptedExisting.Should().BeFalse();
        runner.Calls.Should().Contain(new FakeCliRunner.Call(exe, "model load qwen3.8-27b"));
        runner.Calls.Should().Contain(new FakeCliRunner.Call(exe, "server start --port 7474"));
    }

    [Fact]
    public void CommandShapesArePinned()
    {
        var exe = FoundryCliDetector.CliExecutableName;
        FoundryCliCommands.ModelLoad("qwen3.8-27b").Should().Be((exe, "model load qwen3.8-27b"));
        FoundryCliCommands.ServerStart(7474).Should().Be((exe, "server start --port 7474"));
    }
}
