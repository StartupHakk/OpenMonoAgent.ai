using FluentAssertions;
using OpenMono.Utils;

namespace OpenMono.Tests.Utils;

public class ModelReasoningProfileTests
{
    [Theory]
    [InlineData("MiMo-V2.6-Flash")]
    [InlineData("mimo-v2.6-flash-rl")]
    [InlineData("mimo")]
    [InlineData("XiaomiMiMo/MiMo-V2.6-Flash-RL")]
    public void Mimo_IsBinaryThinking_OnByDefault(string name)
    {
        var p = ModelReasoningProfile.Resolve(name);
        p.Kind.Should().Be(ReasoningKind.BinaryToggle);
        p.DefaultEnabled.Should().BeTrue();
        p.ThinkingTemperature.Should().Be(1.0);
        p.ThinkingTopP.Should().Be(0.95);
        p.PreserveThinking.Should().BeTrue();
    }

    [Fact]
    public void Qwen38_StillUsesEffortLevels()
    {
        var p = ModelReasoningProfile.Resolve("Qwen3.8-Flash-Next");
        p.Kind.Should().Be(ReasoningKind.EffortLevels);
        p.Levels.Should().Contain("xhigh");
    }
}
