using System.Text.Json;
using FluentAssertions;
using OpenMono.Llm;
using OpenMono.Session;

namespace OpenMono.Tests.Session;

public class CheckpointerTests
{
    [Fact]
    public void BuildContextWindow_IgnoresCheckpoint_WhenCutoffExceedsMessageCount()
    {
        var cp = new Checkpointer(new UnusedLlm(), contextSize: 100_000);
        var session = SessionManager.CreateSession();
        session.AddMessage(new Message { Role = MessageRole.System, Content = "sys" });
        session.AddMessage(new Message { Role = MessageRole.User, Content = "hi" });
        session.Checkpoints.Add(new CheckpointEntry
        {
            Id = "c", CreatedAt = DateTime.UtcNow, TurnIndex = 1, CutoffMessageIndex = 100, Summary = "s",
        });

        var window = cp.BuildContextWindow(session);

        // A stale/out-of-range checkpoint (truncated file) must be ignored — fall back
        // to the full transcript rather than emit a summary with zero recent context.
        window.Should().BeEquivalentTo(session.Messages, o => o.WithStrictOrdering());
    }

    [Fact]
    public void BuildContextWindow_UsesCheckpoint_WhenCutoffInRange()
    {
        var cp = new Checkpointer(new UnusedLlm(), contextSize: 100_000);
        var session = SessionManager.CreateSession();
        session.AddMessage(new Message { Role = MessageRole.System, Content = "sys" });
        session.AddMessage(new Message { Role = MessageRole.User, Content = "old" });
        session.AddMessage(new Message { Role = MessageRole.Assistant, Content = "old-reply" });
        session.AddMessage(new Message { Role = MessageRole.User, Content = "recent" });
        session.Checkpoints.Add(new CheckpointEntry
        {
            Id = "c", CreatedAt = DateTime.UtcNow, TurnIndex = 1, CutoffMessageIndex = 3, Summary = "summary-of-old",
        });

        var window = cp.BuildContextWindow(session);

        window.Should().Contain(m => m.Content == "recent");
        window.Should().Contain(m => m.Content != null && m.Content.Contains("summary-of-old"));
        window.Should().NotContain(m => m.Content == "old-reply");
    }

    private sealed class UnusedLlm : ILlmClient
    {
        public IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages, JsonElement? toolDefs, LlmOptions options, CancellationToken ct)
            => throw new InvalidOperationException("LLM must not be called by BuildContextWindow");

        public void Dispose() { }
    }
}
