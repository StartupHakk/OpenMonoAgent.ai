using FluentAssertions;
using OpenMono.Config;
using OpenMono.Utils;

namespace OpenMono.Tests.Utils;

public class ModelReasoningProfileTests
{
    [Fact]
    public void Inkling_DefaultsToLow_AndDoesNotOfferHigh()
    {
        var profile = ModelReasoningProfile.Resolve("inkling-2-mini-sq-10");

        profile.Kind.Should().Be(ReasoningKind.EffortLevels);
        profile.DefaultEnabled.Should().BeTrue();
        profile.DefaultLevel.Should().Be("low");
        profile.Levels.Should().Contain("low");
        profile.Levels.Should().NotContain("high");
    }

    [Fact]
    public void Qwen38_ProfileIsUnchanged()
    {
        var profile = ModelReasoningProfile.Resolve("Qwen3.8-27B-UD-Q4_K_M");

        profile.Kind.Should().Be(ReasoningKind.EffortLevels);
        profile.DefaultLevel.Should().Be("low");
        profile.Levels.Should().Contain(new[] { "off", "low", "medium", "xhigh" });
        profile.ThinkingTemperature.Should().Be(1.0);
    }

    [Fact]
    public void Inkling_AdvertisedHigh_IsDropped_AndDefaultStaysLow()
    {
        var info = new ServerReasoningInfo
        {
            EffortLevels = ["low", "medium", "high", "xhigh"],
            EffortDefaultLevel = "high",
        };

        var profile = ModelReasoningProfile.Resolve("inkling-2-mini-sq-10", info);

        profile.DefaultLevel.Should().Be("low");
        profile.Levels.Should().NotContain("high");
        profile.Levels.Should().Contain("xhigh");
    }

    [Fact]
    public void TopLevelEffort_InklingHighBecomesLow_QwenXhighBecomesHigh()
    {
        ModelReasoningProfile.NormalizeTopLevelEffort("inkling-2-mini-sq-10", "high").Should().Be("low");
        ModelReasoningProfile.NormalizeTopLevelEffort("inkling-2-mini-sq-10", "xhigh").Should().Be("xhigh");
        ModelReasoningProfile.NormalizeTopLevelEffort("inkling-2-mini-sq-10", "low").Should().Be("low");
        ModelReasoningProfile.NormalizeTopLevelEffort("Qwen3.8-27B", "xhigh").Should().Be("high");
        ModelReasoningProfile.NormalizeTopLevelEffort("Qwen3.8-27B", "low").Should().Be("low");
    }

    [Fact]
    public void TemplateEffort_LeavesQwenXhigh_AndRewritesInklingHigh()
    {
        ModelReasoningProfile.NormalizeTemplateEffort("Qwen3.8-27B", "xhigh").Should().Be("xhigh");
        ModelReasoningProfile.NormalizeTemplateEffort("inkling-2-mini-sq-10", "high").Should().Be("low");
        ModelReasoningProfile.NormalizeTemplateEffort("inkling-2-mini-sq-10", "medium").Should().Be("medium");
    }

    [Fact]
    public void SelectRequestEffort_ConfiguredValueWins()
    {
        ModelReasoningProfile.SelectRequestEffort("low", "high", thinking: false, ReasoningKind.None)
            .Should().Be("low");
        ModelReasoningProfile.SelectRequestEffort(null, "medium", thinking: true, ReasoningKind.EffortLevels)
            .Should().Be("medium");
        ModelReasoningProfile.SelectRequestEffort(null, "medium", thinking: false, ReasoningKind.EffortLevels)
            .Should().BeNull();
        ModelReasoningProfile.SelectRequestEffort(null, "medium", thinking: true, ReasoningKind.None)
            .Should().BeNull();
        ModelReasoningProfile.SelectRequestEffort("off", "high", thinking: true, ReasoningKind.EffortLevels)
            .Should().BeNull();
    }

    [Fact]
    public void SessionThinking_UsesConfiguredEffort()
    {
        var profile = ModelReasoningProfile.Resolve("inkling-2-mini-sq-10");
        var seeded = ModelReasoningProfile.SessionThinking(profile, "low");

        seeded.Enabled.Should().BeTrue();
        seeded.Level.Should().Be("low");
    }

    [Fact]
    public void Load_ReadsReasoningEffort_AndEnvOverridesIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "om-effort-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(dir, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "settings.json"), """
        { "llm": { "model": "inkling-2-mini-sq-10", "reasoning_effort": "low" } }
        """);
        var previousData = Environment.GetEnvironmentVariable("OPENMONO_DATA_DIR");
        var previousEffort = Environment.GetEnvironmentVariable("OPENMONO_REASONING_EFFORT");
        try
        {
            Environment.SetEnvironmentVariable("OPENMONO_DATA_DIR", data);
            Environment.SetEnvironmentVariable("OPENMONO_REASONING_EFFORT", null);
            var fromFile = ConfigLoader.Load(dir);
            fromFile.Llm.Model.Should().Be("inkling-2-mini-sq-10");
            fromFile.Llm.ReasoningEffort.Should().Be("low");

            Environment.SetEnvironmentVariable("OPENMONO_REASONING_EFFORT", "medium");
            var fromEnv = ConfigLoader.Load(dir);
            fromEnv.Llm.ReasoningEffort.Should().Be("medium");
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENMONO_DATA_DIR", previousData);
            Environment.SetEnvironmentVariable("OPENMONO_REASONING_EFFORT", previousEffort);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
