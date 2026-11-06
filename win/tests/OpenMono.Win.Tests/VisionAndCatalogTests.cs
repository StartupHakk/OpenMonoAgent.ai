using FluentAssertions;
using Xunit;
using OpenMono.Win.Foundry;

namespace OpenMono.Win.Tests;

public sealed class CatalogClientTests
{
    private static string Exe => FoundryCliDetector.CliExecutableName;

    [Fact]
    public async Task ListsModelsFromCliOutput()
    {
        var runner = new FakeCliRunner();
        runner.Respond(
            Exe, "model list", 0,
            "qwen3.8-27b\nqwen3-4b\nphi-4-mini\n");

        var models = await new FoundryCatalogClient(runner).ListModelsAsync();

        models.Should().BeEquivalentTo("qwen3.8-27b", "qwen3-4b", "phi-4-mini");
    }

    [Fact]
    public async Task SkipsHeadersAndBlankLines()
    {
        var runner = new FakeCliRunner();
        runner.Respond(
            Exe, "model list", 0,
            "Available models:\n\n  qwen3.8-27b\n  qwen3-4b\n");

        var models = await new FoundryCatalogClient(runner).ListModelsAsync();

        models.Should().BeEquivalentTo("qwen3.8-27b", "qwen3-4b");
    }

    [Fact]
    public async Task FailedCliMeansEmptyCatalog()
    {
        var runner = new FakeCliRunner();
        runner.Respond(Exe, "model list", 1, "", "not installed");

        var models = await new FoundryCatalogClient(runner).ListModelsAsync();

        models.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveTargetPrefersTargetOverFallback()
    {
        var runner = new FakeCliRunner();
        runner.Respond(Exe, "model list", 0, "Qwen3.8-27B\nqwen3-4b\n");
        var client = new FoundryCatalogClient(runner);

        var resolved = await client.ResolveTargetAsync("qwen3.8-27b", "qwen3-4b");

        resolved.Should().Be("Qwen3.8-27B");
    }

    [Fact]
    public async Task EmptyCatalogResolvesToNull()
    {
        var runner = new FakeCliRunner();
        var client = new FoundryCatalogClient(runner);

        var resolved = await client.ResolveTargetAsync("qwen3.8-27b", "qwen3-4b");

        resolved.Should().BeNull();
    }
}

public sealed class VisionProbeTests
{
    private static FoundryVisionProbe ProbeWith(FakeHttpHandler handler) =>
        new(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) });

    [Fact]
    public async Task AcceptedWhenServerTakesImageParts()
    {
        var handler = new FakeHttpHandler();
        handler.Respond(
            "/v1/chat/completions",
            FakeHttpHandler.Json("""{"choices":[{"message":{"content":"ok"}}]}"""));

        var result = await ProbeWith(handler).ProbeAsync("http://127.0.0.1:7474", "qwen3.8-27b");

        result.Accepted.Should().BeTrue();
        result.Model.Should().Be("qwen3.8-27b");
    }

    [Fact]
    public async Task RefusedWhenServerRejectsImageParts()
    {
        var handler = new FakeHttpHandler();
        handler.Respond(
            "/v1/chat/completions",
            FakeHttpHandler.Json(
                """{"error":{"message":"image input not supported by this model"}}""",
                System.Net.HttpStatusCode.BadRequest));

        var result = await ProbeWith(handler).ProbeAsync("http://127.0.0.1:7474", "qwen3.8-27b");

        result.Accepted.Should().BeFalse();
        result.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task UnreachableIsNotAnException()
    {
        var probe = new FoundryVisionProbe(new HttpClient(new UnreachableHandler()));

        var result = await probe.ProbeAsync("http://127.0.0.1:7474", "qwen3.8-27b");

        result.Accepted.Should().BeFalse();
        result.StatusCode.Should().BeNull();
    }
}

public sealed class VisionRouterTests
{
    private static VisionProbeResult Accepted(string model = "qwen3.8-27b") =>
        new(true, 200, model, "ok");

    private static VisionProbeResult Refused(string model = "qwen3.8-27b") =>
        new(false, 400, model, "image input not supported by this model");

    [Fact]
    public void TextTurnAlwaysUsesTarget()
    {
        var route = FoundryVisionRouter.SelectModelForTurn(
            hasImages: false, "qwen3.8-27b", "qwen2.5-vl-7b", Refused(), "qwen2.5-vl-7b");

        route.ModelAlias.Should().Be("qwen3.8-27b");
        route.ImagesWillBeSent.Should().BeFalse();
    }

    [Fact]
    public void ImageTurnUsesTargetWhenProbeAccepted()
    {
        var route = FoundryVisionRouter.SelectModelForTurn(
            hasImages: true, "qwen3.8-27b", "qwen2.5-vl-7b", Accepted(), "qwen2.5-vl-7b");

        route.ModelAlias.Should().Be("qwen3.8-27b");
        route.ImagesWillBeSent.Should().BeTrue();
    }

    [Fact]
    public void ImageTurnFallsBackWhenTargetRefused()
    {
        var route = FoundryVisionRouter.SelectModelForTurn(
            hasImages: true, "qwen3.8-27b", "qwen2.5-vl-7b", Refused(), "qwen2.5-vl-7b");

        route.ModelAlias.Should().Be("qwen2.5-vl-7b");
        route.ImagesWillBeSent.Should().BeTrue();
        route.Reason.Should().Contain("refused");
    }

    [Fact]
    public void ImageTurnWithoutFallbackIsExplicitlyUnservable()
    {
        var route = FoundryVisionRouter.SelectModelForTurn(
            hasImages: true, "qwen3.8-27b", "qwen2.5-vl-7b", Refused(), resolvedVisionFallback: null);

        route.ModelAlias.Should().BeNull();
        route.ImagesWillBeSent.Should().BeFalse();
        route.Reason.Should().Contain("NOT sent");
    }
}
