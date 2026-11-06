using FluentAssertions;
using OpenMono.Win.AgentHost;
using Xunit;

namespace OpenMono.Win.Tests;

public sealed class WindowsVisionConfigTests
{
    [Fact]
    public void ExplicitSettingBeatsTheProjector()
    {
        WindowsVisionConfig.Resolve(false, mmprojLoaded: true, "m").VisionEnabled.Should().BeFalse();
        WindowsVisionConfig.Resolve(true, mmprojLoaded: false, "m").VisionEnabled.Should().BeTrue();
    }

    [Fact]
    public void LoadedProjectorEnablesVision()
    {
        var resolution = WindowsVisionConfig.Resolve(null, mmprojLoaded: true, "Qwen3.5-9B-UD-Q4_K_XL");

        resolution.VisionEnabled.Should().BeTrue();
        resolution.Reason.Should().Contain("Qwen3.5-9B-UD-Q4_K_XL");
    }

    [Fact]
    public void NoProjectorDisablesVisionAndSaysHowToGetIt()
    {
        var resolution = WindowsVisionConfig.Resolve(null, mmprojLoaded: false, "Qwen3.5-9B-UD-Q4_K_XL");

        resolution.VisionEnabled.Should().BeFalse();
        resolution.Reason.Should().StartWith("Vision off:").And.Contain("openmono.ps1 setup");
    }
}
