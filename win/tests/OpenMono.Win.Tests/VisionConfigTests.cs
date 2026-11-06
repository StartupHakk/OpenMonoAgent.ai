using FluentAssertions;
using Xunit;
using OpenMono.Win.AgentHost;
using OpenMono.Win.Foundry;

namespace OpenMono.Win.Tests;

public sealed class WindowsImagePolicyTests
{
    [Fact]
    public void TextTurnUsesTheServedModel()
    {
        var route = WindowsImagePolicy.RouteImageTurn(hasImages: false, "qwen3.8-27b", null);

        route.ModelAlias.Should().Be("qwen3.8-27b");
    }

    [Fact]
    public void RequireVisionModelReturnsTheServedModel()
    {
        var route = WindowsImagePolicy.RouteImageTurn(
            true, "qwen3.5-9b", new VisionProbeResult(true, 200, "qwen3.5-9b", "ok"));

        WindowsImagePolicy.RequireVisionModel(route).Should().Be("qwen3.5-9b");
    }

    [Fact]
    public void RequireVisionModelThrowsWithRecoverySteps()
    {
        var route = WindowsImagePolicy.RouteImageTurn(
            true, "qwen3.8-27b", new VisionProbeResult(false, 400, "qwen3.8-27b", "no images"));

        var act = () => WindowsImagePolicy.RequireVisionModel(route);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*NOT sent*only qwen3.8-27b*vision_enabled*");
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
