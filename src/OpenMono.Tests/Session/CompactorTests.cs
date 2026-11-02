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

    // Reproduces session 3a16e08ec244: very few user turns, each carrying huge tool outputs.
    // Old behaviour kept "the last 4 user turns" verbatim, which alone exceeded the whole
    // window, so even a perfect summary of the rest could not fit. The keep-window is now
    // capped in tokens and the overflow is summarized (in chunks), so compaction lands under
    // the window.
    [Fact]
    public async Task CompactAsync_FewUserTurnsWithHugeToolOutputs_LandsUnderTheWindow()
    {
        const int ctx = 98304; // the real llama-server window from that session
        var llm = new RecordingSummaryLlm();
        var compactor = new Compactor(llm, contextSize: ctx);
        var session = new SessionState();
        session.AddMessage(new Message { Role = MessageRole.System, Content = new string('s', 3000) });

        const int toolChars = 90_000; // ~22.5K tokens per tool output
        for (var i = 0; i < 9; i++)
        {
            session.AddMessage(new Message { Role = MessageRole.User, Content = $"turn {i} request" });
            session.AddMessage(new Message { Role = MessageRole.Assistant, Content = $"working on turn {i}" });
            for (var t = 0; t < 3; t++)
            {
                session.AddMessage(new Message
                {
                    Role = MessageRole.Tool,
                    ToolCallId = $"call_{i}_{t}",
                    ToolName = "Bash",
                    Content = $"MARKER_T{i}_{t}" + new string('x', toolChars),
                });
            }
        }

        var (compacted, report) = await compactor.CompactAsync(session);

        // Precondition: the session was far over the window (the original failure).
        report.TokensBefore.Should().BeGreaterThan(ctx);

        // The compacted session fits comfortably under the window.
        report.TokensAfter.Should().BeLessThan((int)(ctx * 0.80),
            "compaction must land under the window, not just shrink it");

        // Many chunks of the huge history were summarized, not dropped.
        llm.Calls.Should().BeGreaterThanOrEqualTo(2, "the summarization should be split into multiple chunks");
        llm.AllReceivedText.Should().Contain(text => text.Contains("MARKER_T0_0"),
            "the OLDEST tool output must still reach the summarizer — it is context, not garbage");

        // The most recent user turn is kept verbatim.
        compacted.Messages.Should().Contain(m =>
            m.Role == MessageRole.User && m.Content == "turn 8 request");
    }

    [Fact]
    public async Task CompactAsync_SingleOversizedToolMessage_IsTruncatedButStillSummarized()
    {
        const int ctx = 98304;
        var llm = new RecordingSummaryLlm();
        var compactor = new Compactor(llm, contextSize: ctx);
        var session = new SessionState();
        session.AddMessage(new Message { Role = MessageRole.System, Content = "sys" });

        // Four small pre-turns so there is a real history to summarize.
        for (var i = 0; i < 4; i++)
        {
            session.AddMessage(new Message { Role = MessageRole.User, Content = $"pre {i}" });
            session.AddMessage(new Message { Role = MessageRole.Assistant, Content = $"ok {i}" });
        }

        // One turn whose tool output alone exceeds the per-chunk summary budget.
        session.AddMessage(new Message { Role = MessageRole.User, Content = "run the big job" });
        session.AddMessage(new Message
        {
            Role = MessageRole.Tool,
            ToolCallId = "call_huge",
            ToolName = "Bash",
            Content = "MARKER_HUGE" + new string('y', 400_000), // ~100K tokens — bigger than one chunk
        });
        session.AddMessage(new Message { Role = MessageRole.Assistant, Content = "job done" });

        // A final small turn (kept verbatim) so the huge turn falls into the summarized half.
        session.AddMessage(new Message { Role = MessageRole.User, Content = "latest question" });

        var (compacted, report) = await compactor.CompactAsync(session);

        llm.Calls.Should().BeGreaterThanOrEqualTo(1);
        llm.AllReceivedText.Should().Contain(text => text.Contains("MARKER_HUGE"),
            "an oversized message must be truncated and summarized, never silently dropped");
        llm.AllReceivedText.Should().Contain(text => text.Contains("truncated"),
            "the truncation must be flagged in the summarized content");
        report.TokensAfter.Should().BeLessThan((int)(ctx * 0.80));
    }

    [Fact]
    public async Task GetRecentTurns_SizeCap_PreventsTheKeepWindowFromExceedingItsBudget()
    {
        const int ctx = 98304;
        var compactor = new Compactor(new SummaryLlm(), contextSize: ctx);
        var session = new SessionState();
        session.AddMessage(new Message { Role = MessageRole.System, Content = "sys" });

        for (var i = 0; i < 6; i++)
        {
            session.AddMessage(new Message { Role = MessageRole.User, Content = $"turn {i}" });
            session.AddMessage(new Message
            {
                Role = MessageRole.Tool,
                ToolCallId = $"call_{i}",
                ToolName = "Bash",
                Content = new string('z', 60_000), // ~15K tokens each
            });
        }

        var (compacted, report) = await compactor.CompactAsync(session);

        var systemCount = compacted.Messages.Count(m => m.Role == MessageRole.System);
        var keptWindow = compacted.Messages.Skip(systemCount + 2).ToList(); // skip summary + ack
        keptWindow.Should().NotBeEmpty();
        var keptTokens = TokenEstimator.EstimateMessages(keptWindow);
        keptTokens.Should().BeLessThanOrEqualTo((int)(ctx * 0.30) + 1,
            "the verbatim keep-window must respect its token budget even when the recent turns are huge");
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

    private sealed class RecordingSummaryLlm : ILlmClient
    {
        public int Calls { get; private set; }
        public List<string> ReceivedTexts { get; } = new();
        public IEnumerable<string> AllReceivedText => ReceivedTexts;

        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages, JsonElement? toolDefs, LlmOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            var user = messages.Last(m => m.Role == MessageRole.User);
            ReceivedTexts.Add(user.Content ?? "");
            yield return new StreamChunk { TextDelta = "summary of this part", IsComplete = true };
            await Task.CompletedTask;
        }

        public void Dispose() { }
    }
}
