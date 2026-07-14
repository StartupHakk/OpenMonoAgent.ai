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

    [Fact]
    public async Task CreateCheckpointAsync_KeepsPendingToolCall_VisibleInContextWindow()
    {
        var cp = new Checkpointer(new SummaryLlm(), contextSize: 100_000);
        var session = SessionManager.CreateSession();
        session.AddMessage(new Message { Role = MessageRole.System, Content = "sys" });

        for (var i = 0; i < 2; i++)
        {
            session.AddMessage(new Message { Role = MessageRole.User, Content = $"pre {i}" });
            session.AddMessage(new Message { Role = MessageRole.Assistant, Content = $"pre-reply {i}" });
        }

        session.AddMessage(new Message { Role = MessageRole.User, Content = "delete the prod table" });
        session.AddMessage(new Message
        {
            Role = MessageRole.Assistant,
            ToolCalls = [new ToolCall { Id = "call_pending", Name = "DangerousTool", Arguments = "{}" }],
        });

        for (var i = 0; i < 5; i++)
        {
            session.AddMessage(new Message { Role = MessageRole.User, Content = $"meanwhile {i}" });
            session.AddMessage(new Message { Role = MessageRole.Assistant, Content = $"ok {i}" });
        }

        await cp.CreateCheckpointAsync(session, CancellationToken.None);
        var window = cp.BuildContextWindow(session);

        window.Should().Contain(m => m.ToolCalls != null && m.ToolCalls.Any(c => c.Id == "call_pending"),
            "a tool call still awaiting a permission decision must stay visible in context — hiding it " +
            "behind a checkpoint summary would desync the model's view of pending tool_use/tool_result pairs");
    }

    private sealed class UnusedLlm : ILlmClient
    {
        public IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages, JsonElement? toolDefs, LlmOptions options, CancellationToken ct)
            => throw new InvalidOperationException("LLM must not be called by BuildContextWindow");

        public void Dispose() { }
    }

    private sealed class SummaryLlm : ILlmClient
    {
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages, JsonElement? toolDefs, LlmOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            yield return new StreamChunk { TextDelta = "summary of old turns", IsComplete = true };
            await Task.CompletedTask;
        }

        public void Dispose() { }
    }
}
