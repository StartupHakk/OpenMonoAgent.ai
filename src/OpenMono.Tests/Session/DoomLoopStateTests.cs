using FluentAssertions;
using OpenMono.Session;

namespace OpenMono.Tests.Session;

public class DoomLoopStateTests
{
    [Fact]
    public void Tier_Boundaries_MatchConfirmedThresholds()
    {
        DoomLoopTierExtensions.ForHits(0).Should().Be(DoomLoopTier.None);
        DoomLoopTierExtensions.ForHits(1).Should().Be(DoomLoopTier.Nudge);
        DoomLoopTierExtensions.ForHits(2).Should().Be(DoomLoopTier.Nudge);
        DoomLoopTierExtensions.ForHits(3).Should().Be(DoomLoopTier.StrongNudge);
        DoomLoopTierExtensions.ForHits(4).Should().Be(DoomLoopTier.StrongNudge);
        DoomLoopTierExtensions.ForHits(5).Should().Be(DoomLoopTier.Escalate);
        DoomLoopTierExtensions.ForHits(9).Should().Be(DoomLoopTier.Escalate);
    }

    [Fact]
    public void RecordHit_EachDetection_AccumulatesTowardEscalation()
    {
        var state = new DoomLoopState();
        state.RecordHit().Should().Be(DoomLoopTier.Nudge);
        state.RecordHit().Should().Be(DoomLoopTier.Nudge);
        state.RecordHit().Should().Be(DoomLoopTier.StrongNudge);
        state.RecordHit().Should().Be(DoomLoopTier.StrongNudge);
        state.RecordHit().Should().Be(DoomLoopTier.Escalate);
        state.ConsecutiveHits.Should().Be(5);
    }

    [Fact]
    public void DetectorFirings_AccumulateAcrossAlternatingSignatures()
    {
        // A model stuck in a period-2 loop (A,B,A,B,…) never repeats the same signature back-to-back.
        // The detector fires on the looping window regardless, so the tier must still climb.
        var state = new DoomLoopState();
        state.RecordHit(); // firing on "a" (e.g. A,B,A,B window)
        state.RecordHit(); // next firing on "b"
        state.RecordHit(); // next firing on "a"
        state.Tier.Should().Be(DoomLoopTier.StrongNudge);
        state.ConsecutiveHits.Should().Be(3);
    }

    [Fact]
    public void Reset_ClearsStreak()
    {
        var state = new DoomLoopState();
        state.RecordHit();
        state.RecordHit();
        state.Reset();
        state.ConsecutiveHits.Should().Be(0);
        state.Tier.Should().Be(DoomLoopTier.None);
    }

    [Fact]
    public void SharedPrompts_ProduceEveryTierVariant()
    {
        DoomLoopPrompts.Nudge("T", DoomLoopTier.Nudge).Should().Contain("Doom loop (1st)");
        DoomLoopPrompts.Nudge("T", DoomLoopTier.StrongNudge).Should().Contain("Doom loop (escalated)");
        DoomLoopPrompts.Max("T").Should().Contain("Doom loop (max)");
        DoomLoopPrompts.NudgeLabel(DoomLoopTier.Nudge).Should().Be("nudging");
        DoomLoopPrompts.NudgeLabel(DoomLoopTier.Escalate).Should().Be("escalating the nudge");
    }
}