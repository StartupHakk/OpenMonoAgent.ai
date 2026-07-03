using FluentAssertions;
using OpenMono.Session;

namespace OpenMono.Tests.Session;

public class SessionReattachTests
{
    [Fact]
    public void Apply_AdoptsLoadedIdentityAndContent_PreservingCurrentSystemPrompt()
    {
        var live = new SessionState();
        live.AddMessage(new Message { Role = MessageRole.System, Content = "CURRENT system prompt" });

        var loaded = new SessionState
        {
            Id = "sess_abc123",
            StartedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Model = "gpt-4o",
        };
        loaded.AddMessage(new Message { Role = MessageRole.System, Content = "OLD system prompt" });
        loaded.AddMessage(new Message { Role = MessageRole.User, Content = "earlier work" });
        loaded.AddMessage(new Message { Role = MessageRole.Assistant, Content = "did it" });
        loaded.TurnCount = 3;
        loaded.TotalTokensUsed = 999;
        loaded.Checkpoints.Add(new CheckpointEntry
        {
            Id = "c", CreatedAt = DateTime.UtcNow, TurnIndex = 1, CutoffMessageIndex = 2, Summary = "sum",
        });
        loaded.CheckpointCutoffIndex = 2;

        SessionReattach.Apply(live, loaded);

        live.Id.Should().Be("sess_abc123");
        live.StartedAt.Should().Be(loaded.StartedAt);
        live.Model.Should().Be("gpt-4o");
        live.TurnCount.Should().Be(3);
        live.TotalTokensUsed.Should().Be(999);
        live.Checkpoints.Should().HaveCount(1);
        live.CheckpointCutoffIndex.Should().Be(2);

        live.Messages[0].Role.Should().Be(MessageRole.System);
        live.Messages[0].Content.Should().Be("CURRENT system prompt");
        live.Messages.Should().Contain(m => m.Content == "earlier work");
        live.Messages.Should().NotContain(m => m.Content == "OLD system prompt");
    }

    [Fact]
    public void Apply_KeepsLoadedSystemPrompt_WhenLiveHasNone()
    {
        var live = new SessionState(); // no system message yet

        var loaded = new SessionState { Id = "sess_x", StartedAt = DateTime.UtcNow };
        loaded.AddMessage(new Message { Role = MessageRole.System, Content = "LOADED system" });
        loaded.AddMessage(new Message { Role = MessageRole.User, Content = "hi" });

        SessionReattach.Apply(live, loaded);

        live.Messages.Should().Contain(m => m.Role == MessageRole.System && m.Content == "LOADED system");
        live.Messages.Should().Contain(m => m.Content == "hi");
    }

    [Fact]
    public void Apply_RepairsDanglingToolCalls_FromInterruptedSession()
    {
        var live = new SessionState();
        live.AddMessage(new Message { Role = MessageRole.System, Content = "sys" });

        var loaded = new SessionState { Id = "sess_dead", StartedAt = DateTime.UtcNow };
        loaded.AddMessage(new Message { Role = MessageRole.User, Content = "go" });
        loaded.AddMessage(new Message
        {
            Role = MessageRole.Assistant,
            ToolCalls = new() { new ToolCall { Id = "c1", Name = "Bash", Arguments = "{}" } },
        });
        // crashed before the tool result was written

        SessionReattach.Apply(live, loaded);

        live.Messages.Should().Contain(m => m.Role == MessageRole.Tool && m.ToolCallId == "c1");
    }
}
