using FluentAssertions;
using OpenMono.HostBridge;

namespace OpenMono.Tests.HostBridge;

public class HostExecSuppressionTrackerTests
{
    [Fact]
    public void HostExecCarrier_ToolEnd_IsSuppressed()
    {
        var tracker = new HostExecSuppressionTracker();
        tracker.TrackAskUserStart("call_1");

        tracker.LinkPrompt(isHostExec: true).Should().Be("call_1");
        tracker.ConsumeIfSuppressed("call_1").Should().BeTrue();
        // Second consume: already gone.
        tracker.ConsumeIfSuppressed("call_1").Should().BeFalse();
    }

    [Fact]
    public void GenuineQuestion_IsNeverSuppressed()
    {
        var tracker = new HostExecSuppressionTracker();
        tracker.TrackAskUserStart("call_2");

        tracker.LinkPrompt(isHostExec: false).Should().Be("call_2");
        tracker.ConsumeIfSuppressed("call_2").Should().BeFalse();
    }

    [Fact]
    public void PromptWithoutAskUserOrigin_LinksNull()
    {
        var tracker = new HostExecSuppressionTracker();

        tracker.LinkPrompt(isHostExec: true).Should().BeNull();
        tracker.LinkPrompt(isHostExec: false).Should().BeNull();
    }

    [Fact]
    public void FifoOrder_PairsConcurrentAskUsers()
    {
        var tracker = new HostExecSuppressionTracker();
        tracker.TrackAskUserStart("call_A");
        tracker.TrackAskUserStart("call_B");

        tracker.LinkPrompt(isHostExec: false).Should().Be("call_A");
        tracker.LinkPrompt(isHostExec: true).Should().Be("call_B");
        tracker.ConsumeIfSuppressed("call_A").Should().BeFalse();
        tracker.ConsumeIfSuppressed("call_B").Should().BeTrue();
    }

    [Fact]
    public void Clear_ResetsPairingState()
    {
        var tracker = new HostExecSuppressionTracker();
        tracker.TrackAskUserStart("call_1");
        tracker.LinkPrompt(isHostExec: true);
        tracker.Clear();

        tracker.LinkPrompt(isHostExec: false).Should().BeNull();
        tracker.ConsumeIfSuppressed("call_1").Should().BeFalse();
    }
}
