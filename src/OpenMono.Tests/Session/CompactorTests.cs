using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Llm;
using OpenMono.Session;

namespace OpenMono.Tests.Session;

public class CompactorTests
{
    [Fact]
    public async Task CompactAsync_NeverEvictsAnUnansweredToolCall_EvenWhenItFallsOutsideTheKeepWindow()
    {
        var compactor = new Compactor(new SummaryLlm(), contextSize: 100_000);
        var session = new SessionState();
        session.AddMessage(new Message { Role = MessageRole.System, Content = "sys" });

        // Two ordinary turns of real history — something to actually summarize.
        for (var i = 0; i < 2; i++)
        {
            session.AddMessage(new Message { Role = MessageRole.User, Content = $"pre {i}" });
            session.AddMessage(new Message { Role = MessageRole.Assistant, Content = $"pre-reply {i}" });
        }

        // A risky call that's still awaiting a permission decision.
        session.AddMessage(new Message { Role = MessageRole.User, Content = "delete the prod table" });
        session.AddMessage(new Message
        {
            Role = MessageRole.Assistant,
            ToolCalls = [new ToolCall { Id = "call_pending", Name = "DangerousTool", Arguments = "{}" }],
        });

        // Five more turns pass before the user gets back to answering that permission prompt —
        // enough to push it outside the default 4-turn "recent" window.
        for (var i = 0; i < 5; i++)
        {
            session.AddMessage(new Message { Role = MessageRole.User, Content = $"meanwhile {i}" });
            session.AddMessage(new Message { Role = MessageRole.Assistant, Content = $"ok {i}" });
        }

        var (compacted, report) = await compactor.CompactAsync(session);

        report.MessagesCompressed.Should().BeGreaterThan(0, "the two 'pre' turns should have actually been summarized");
        var pending = compacted.Messages.FirstOrDefault(m =>
            m.ToolCalls?.Any(c => c.Id == "call_pending") == true);
        pending.Should().NotBeNull(
            "a tool call still awaiting a permission decision must survive compaction — " +
            "resolving it later needs to find this exact message still in history");
    }

    private sealed class SummaryLlm : ILlmClient
    {
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages, JsonElement? toolDefs, LlmOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new StreamChunk { TextDelta = "summary of old turns", IsComplete = true };
            await Task.CompletedTask;
        }

        public void Dispose() { }
    }
}
