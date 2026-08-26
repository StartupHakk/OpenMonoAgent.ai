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
    public void RecordHit_SameSignature_Accumulates()
    {
        var state = new DoomLoopState();
        state.RecordHit("sig").Should().Be(DoomLoopTier.Nudge);
        state.RecordHit("sig").Should().Be(DoomLoopTier.Nudge);
        state.RecordHit("sig").Should().Be(DoomLoopTier.StrongNudge);
        state.RecordHit("sig").Should().Be(DoomLoopTier.StrongNudge);
        state.RecordHit("sig").Should().Be(DoomLoopTier.Escalate);
        state.ConsecutiveHits.Should().Be(5);
    }

    [Fact]
    public void RecordHit_DifferentSignature_RestartsStreak()
    {
        var state = new DoomLoopState();
        state.RecordHit("a");
        state.RecordHit("a");
        state.RecordHit("a"); // StrongNudge (3)
        state.RecordHit("b").Should().Be(DoomLoopTier.Nudge, "a new signature restarts the streak at 1");
        state.ConsecutiveHits.Should().Be(1);
    }

    [Fact]
    public void Reset_ClearsStreak()
    {
        var state = new DoomLoopState();
        state.RecordHit("a");
        state.RecordHit("a");
        state.Reset();
        state.ConsecutiveHits.Should().Be(0);
        state.Tier.Should().Be(DoomLoopTier.None);
    }
}
