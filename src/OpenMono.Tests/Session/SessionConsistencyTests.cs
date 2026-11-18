using FluentAssertions;
using OpenMono.Session;

namespace OpenMono.Tests.Session;

public class SessionConsistencyTests
{
    [Fact]
    public void Repair_Normalizes_Orphan_Implementing_Plans()
    {
        var session = SessionManager.CreateSession();
        session.Meta.Plans.Add(new PlanRecord
        {
            Id = "plan_done",
            Content = "finished plan",
            CreatedAtUtc = System.DateTime.UtcNow,
            Status = PlanStatus.Implementing,
        });
        session.Meta.Plans.Add(new PlanRecord
        {
            Id = "plan_pending",
            Content = "awaiting decision",
            CreatedAtUtc = System.DateTime.UtcNow,
            Status = PlanStatus.Pending,
        });

        SessionConsistency.Repair(session);

        session.Meta.Plans.Single(p => p.Id == "plan_done").Status.Should().Be(PlanStatus.Abandoned);
        session.Meta.Plans.Single(p => p.Id == "plan_pending").Status.Should().Be(PlanStatus.Pending);
    }

    [Fact]
    public void Repair_SynthesizesResult_ForTrailingUnansweredToolCall()
    {
        var session = SessionManager.CreateSession();
        session.AddMessage(new Message { Role = MessageRole.User, Content = "run it" });
        session.AddMessage(new Message
        {
            Role = MessageRole.Assistant,
            Content = "calling tool",
            ToolCalls = new List<ToolCall> { new() { Id = "call_1", Name = "Bash", Arguments = "{}" } },
        });
        // process crashed before the tool result was recorded

        var count = SessionConsistency.Repair(session);

        count.Should().Be(1);
        var last = session.Messages[^1];
        last.Role.Should().Be(MessageRole.Tool);
        last.ToolCallId.Should().Be("call_1");
        last.ToolName.Should().Be("Bash");
        last.Content.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Repair_IsNoOp_WhenEveryToolCallIsAnswered()
    {
        var session = SessionManager.CreateSession();
        session.AddMessage(new Message { Role = MessageRole.User, Content = "run it" });
        session.AddMessage(new Message
        {
            Role = MessageRole.Assistant,
            ToolCalls = new List<ToolCall> { new() { Id = "call_1", Name = "Bash", Arguments = "{}" } },
        });
        session.AddMessage(new Message
        {
            Role = MessageRole.Tool,
            ToolCallId = "call_1",
            ToolName = "Bash",
            Content = "done",
        });
        var before = session.Messages.Count;

        var count = SessionConsistency.Repair(session);

        count.Should().Be(0);
        session.Messages.Should().HaveCount(before);
    }

    [Fact]
    public void Repair_SynthesizesOnlyMissingResult_WhenPartiallyAnswered()
    {
        var session = SessionManager.CreateSession();
        session.AddMessage(new Message
        {
            Role = MessageRole.Assistant,
            ToolCalls = new List<ToolCall>
            {
                new() { Id = "c1", Name = "Read", Arguments = "{}" },
                new() { Id = "c2", Name = "Bash", Arguments = "{}" },
            },
        });
        session.AddMessage(new Message
        {
            Role = MessageRole.Tool,
            ToolCallId = "c1",
            ToolName = "Read",
            Content = "file contents",
        });

        var count = SessionConsistency.Repair(session);

        count.Should().Be(1);
        session.Messages
            .Where(m => m.Role == MessageRole.Tool)
            .Select(m => m.ToolCallId)
            .Should().BeEquivalentTo(new[] { "c1", "c2" });
    }
}
