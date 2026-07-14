using FluentAssertions;
using OpenMono.Session;

namespace OpenMono.Tests.Session;

public class SessionDigestTests
{
    [Fact]
    public void DeriveTitle_UsesFirstUserMessage_CollapsedAndTrimmed()
    {
        var messages = new List<Message>
        {
            new() { Role = MessageRole.System, Content = "you are a helper" },
            new() { Role = MessageRole.User, Content = "  Fix the\nlogin bug  " },
            new() { Role = MessageRole.Assistant, Content = "sure" },
        };

        SessionDigest.DeriveTitle(messages).Should().Be("Fix the login bug");
    }

    [Fact]
    public void DeriveTitle_TruncatesWithEllipsis_WhenLongerThanMax()
    {
        var messages = new List<Message>
        {
            new() { Role = MessageRole.User, Content = new string('a', 100) },
        };

        var title = SessionDigest.DeriveTitle(messages, maxLength: 80);

        title.Should().HaveLength(81);
        title.Should().Be(new string('a', 80) + "…");
    }

    [Fact]
    public void DeriveTitle_ReturnsEmpty_WhenNoUserMessage()
    {
        var messages = new List<Message>
        {
            new() { Role = MessageRole.System, Content = "system only" },
        };

        SessionDigest.DeriveTitle(messages).Should().BeEmpty();
    }

    [Fact]
    public void DeriveLatestSummary_ReturnsLastCheckpointSummary()
    {
        var checkpoints = new List<CheckpointEntry>
        {
            new() { Id = "c1", CreatedAt = DateTime.UtcNow, TurnIndex = 1, CutoffMessageIndex = 2, Summary = "first" },
            new() { Id = "c2", CreatedAt = DateTime.UtcNow, TurnIndex = 4, CutoffMessageIndex = 8, Summary = "second" },
        };

        SessionDigest.DeriveLatestSummary(checkpoints).Should().Be("second");
    }

    [Fact]
    public void DeriveLatestSummary_ReturnsNull_WhenNoCheckpoints()
    {
        SessionDigest.DeriveLatestSummary(new List<CheckpointEntry>()).Should().BeNull();
    }
}
