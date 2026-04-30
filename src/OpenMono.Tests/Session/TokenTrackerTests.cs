using FluentAssertions;
using OpenMono.Session;

namespace OpenMono.Tests.Session;

public class TokenTrackerTests
{
    [Fact]
    public void RecordUsage_AccumulatesTokens()
    {
        var tracker = new TokenTracker();
        tracker.RecordUsage(100, 50);
        tracker.RecordUsage(200, 75);

        tracker.TotalPromptTokens.Should().Be(300);
        tracker.TotalCompletionTokens.Should().Be(125);
        tracker.TotalTokens.Should().Be(425);
        tracker.ApiCalls.Should().Be(2);
    }

    [Fact]
    public void RecordToolUse_CountsInvocations()
    {
        var tracker = new TokenTracker();
        tracker.RecordToolUse("FileRead");
        tracker.RecordToolUse("FileRead");
        tracker.RecordToolUse("Bash");

        tracker.ToolUsageCounts["FileRead"].Should().Be(2);
        tracker.ToolUsageCounts["Bash"].Should().Be(1);
    }

    [Fact]
    public void GetSummary_IncludesAllStats()
    {
        var tracker = new TokenTracker();
        tracker.RecordUsage(100, 50);
        tracker.RecordToolUse("FileRead");
        tracker.FilesModified = 2;
        tracker.FilesCreated = 1;

        var summary = tracker.GetSummary(DateTime.UtcNow.AddMinutes(-5));

        summary.Should().Contain("API calls:");
        summary.Should().Contain("Prompt tokens:");
        summary.Should().Contain("FileRead");
        summary.Should().Contain("Files created:");
    }
}
