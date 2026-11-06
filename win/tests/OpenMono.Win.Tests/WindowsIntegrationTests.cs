using FluentAssertions;
using Xunit;
using OpenMono.Win.AgentHost;
using OpenMono.Win.Foundry;

namespace OpenMono.Win.Tests;

public sealed class WindowsImagePolicyTests
{
    [Fact]
    public void TextTurnRoutesToTarget()
    {
        var route = WindowsImagePolicy.RouteImageTurn(
            hasImages: false, "qwen3.8-27b", null, "qwen2.5-vl-7b");

        route.ModelAlias.Should().Be("qwen3.8-27b");
    }

    [Fact]
    public void RequireVisionModelReturnsRoutedModel()
    {
        var route = FoundryVisionRouter.SelectModelForTurn(
            true, "qwen3.8-27b", "qwen2.5-vl-7b",
            new VisionProbeResult(false, 400, "qwen3.8-27b", "no images"), "qwen2.5-vl-7b");

        WindowsImagePolicy.RequireVisionModel(route, "qwen2.5-vl-7b").Should().Be("qwen2.5-vl-7b");
    }

    [Fact]
    public void RequireVisionModelThrowsWithRecoverySteps()
    {
        var route = FoundryVisionRouter.SelectModelForTurn(
            true, "qwen3.8-27b", "qwen2.5-vl-7b",
            new VisionProbeResult(false, 400, "qwen3.8-27b", "no images"), null);

        var act = () => WindowsImagePolicy.RequireVisionModel(route, "qwen2.5-vl-7b");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*foundry model load qwen2.5-vl-7b*vision_enabled*");
    }
}

public sealed class WindowsVisionConfigTests
{
    [Fact]
    public void ExplicitSettingBeatsProbe()
    {
        var accepted = new VisionProbeResult(true, 200, "qwen3.8-27b", "ok");

        WindowsVisionConfig.ApplyProbeResult(false, accepted).VisionEnabled.Should().BeFalse();
        WindowsVisionConfig.ApplyProbeResult(true, null).VisionEnabled.Should().BeTrue();
    }

    [Fact]
    public void AcceptedProbeEnablesVision()
    {
        var accepted = new VisionProbeResult(true, 200, "qwen3.8-27b", "ok");

        var resolution = WindowsVisionConfig.ApplyProbeResult(null, accepted);

        resolution.VisionEnabled.Should().BeTrue();
        resolution.Reason.Should().Contain("qwen3.8-27b");
    }

    [Fact]
    public void RefusedProbeDisablesVision()
    {
        var refused = new VisionProbeResult(false, 400, "qwen3.8-27b", "image input not supported");

        var resolution = WindowsVisionConfig.ApplyProbeResult(null, refused);

        resolution.VisionEnabled.Should().BeFalse();
        resolution.Reason.Should().Contain("inference.vision_enabled=true");
    }
}
