using FluentAssertions;
using OpenMono.Llm;
using OpenMono.Session;
using NSubstitute;

namespace OpenMono.Tests.Session;

public class CompactorTests
{
    [Fact]
    public void NeedsCompaction_UnderThreshold_ReturnsFalse()
    {
        var llm = Substitute.For<ILlmClient>();
        var compactor = new Compactor(llm, contextSize: 32768);
        var session = new SessionState();
        session.AddMessage(new Message { Role = MessageRole.User, Content = "Hello" });

        compactor.NeedsCompaction(session).Should().BeFalse();
    }

    [Fact]
    public void NeedsCompaction_OverThreshold_ReturnsTrue()
    {
        var llm = Substitute.For<ILlmClient>();
        var compactor = new Compactor(llm, contextSize: 100);

        var session = new SessionState();

        session.AddMessage(new Message { Role = MessageRole.User, Content = new string('x', 500) });

        compactor.NeedsCompaction(session).Should().BeTrue();
    }

    [Fact]
    public async Task CompactAsync_TooFewMessages_ReturnsOriginal()
    {
        var llm = Substitute.For<ILlmClient>();
        var compactor = new Compactor(llm, contextSize: 100);
        var session = new SessionState();
        session.AddMessage(new Message { Role = MessageRole.System, Content = "System" });
        session.AddMessage(new Message { Role = MessageRole.User, Content = "Hi" });

        var result = await compactor.CompactAsync(session, CancellationToken.None);

        result.Messages.Should().HaveCount(2);
    }
}
