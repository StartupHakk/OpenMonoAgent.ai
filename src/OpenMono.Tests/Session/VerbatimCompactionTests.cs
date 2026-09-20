using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Decisions;
using OpenMono.Llm;
using OpenMono.Session;

namespace OpenMono.Tests.Session;

/// <summary>
/// Phase 4 verbatim compaction (Kinza section 9.5). The (tool-call,
/// result) relevance pass runs through the active
/// <see cref="IDecisionBackend"/> — the heuristic here — asking two
/// scoring questions per pair, never LLM generation. User/assistant text
/// is always kept verbatim and in order; only tool pairs are dropped or
/// truncated, and only below the conservative code-owned threshold.
/// </summary>
public class VerbatimCompactionTests
{
    private static Message User(string c) => new() { Role = MessageRole.User, Content = c };
    private static Message Assistant(string c) => new() { Role = MessageRole.Assistant, Content = c };
    private static Message Call(string id, string name, string args) => new()
    {
        Role = MessageRole.Assistant,
        ToolCalls = [new ToolCall { Id = id, Name = name, Arguments = args }],
    };
    private static Message Result(string callId, string toolName, string content) => new()
    {
        Role = MessageRole.Tool, ToolCallId = callId, ToolName = toolName, Content = content,
    };

    private static List<Message> RecentTurnsAboutAuth() =>
    [
        User("auth middleware redirect loop"),
        Assistant("auth middleware redirect loop"),
        User("auth middleware redirect loop"),
        Assistant("auth middleware redirect loop"),
        User("auth middleware redirect loop"),
        Assistant("auth middleware redirect loop"),
        User("auth middleware redirect loop"),
        Assistant("auth middleware redirect loop"),
    ];

    private static SessionState BuildSession(List<Message> oldWindow, List<Message> recent)
    {
        var s = new SessionState();
        s.AddMessage(User("sys-bootstrap"));
        foreach (var m in oldWindow)
            s.AddMessage(m);
        foreach (var m in recent)
            s.AddMessage(m);
        return s;
    }

    private static Compactor VerbatimCompactor(VerbatimCompactionOptions? opts = null) =>
        new(new FailingLlm(), contextSize: 100_000, backend: null, verbatimOptions: opts);

    [Fact]
    public async Task Stale_Pair_Dropped_Fresh_Pair_Kept_Byte_Identical()
    {
        var staleResult = string.Concat(Enumerable.Repeat(
            "database migration schema_migrations row applied at version ", 40));
        var freshContent = "auth middleware redirect loop: tool result with exact content, " +
            "file paths src/Auth/Middleware.cs, error messages about the redirect loop, " +
            "exact commands to reproduce.";
        var oldWindow = new List<Message>
        {
            User("first investigage the database migration history"),
            Assistant("checking the database migration history now"),
            Call("c-stale", "DbQuery", "{\"sql\":\"SELECT * FROM schema_migrations\"}"),
            Result("c-stale", "DbQuery", staleResult),
            User("now pivot to the auth middleware redirect loop"),
            Assistant("pivoting to the auth middleware redirect loop"),
            Call("c-fresh", "FileRead", "{\"path\":\"src/Auth/Middleware.cs\"}"),
            Result("c-fresh", "FileRead", freshContent),
        };
        var session = BuildSession(oldWindow, RecentTurnsAboutAuth());

        var (compacted, report) = await VerbatimCompactor()
            .CompactWithVerbatimFirstAsync(session);

        report.Strategy.Should().Be("verbatim");
        var texts = compacted.Messages.Select(m =>
            m.Content ?? string.Join(",", m.ToolCalls?.Select(c => c.Id) ?? [])).ToList();
        texts.Should().NotContain(t => t.Contains("schema_migrations"),
            "the stale database-migration pair is irrelevant to the auth-loop context and must be dropped");
        texts.Should().NotContain("c-stale", "the stale call itself goes with its result");
        var kept = compacted.Messages.FirstOrDefault(m => m.ToolCallId == "c-fresh");
        kept.Should().NotBeNull("the fresh pair is relevant and verbatim-worthy");
        kept!.Content.Should().Be(freshContent, "retained content is byte-identical, never paraphrased");
        compacted.Messages.Should().Contain(m => m.Content == "first investigage the database migration history",
            "user/assistant text is always kept verbatim");
    }

    [Fact]
    public async Task Relevant_But_Not_Verbatim_Worthy_Result_Is_Truncated_With_Marker()
    {
        var longProse = "auth middleware redirect loop " + string.Concat(Enumerable.Repeat(
            "prose cookies ", 30));
        var oldWindow = new List<Message>
        {
            User("look at the auth middleware redirect loop"),
            Assistant("reading the auth middleware redirect loop output"),
            Call("c-long", "LogTail", "{\"service\":\"auth\"}"),
            Result("c-long", "LogTail", longProse),
            User("also check the auth middleware redirect loop again"),
            Assistant("double-checking the auth middleware redirect loop"),
        };
        var session = BuildSession(oldWindow, RecentTurnsAboutAuth());
        var opts = new VerbatimCompactionOptions(TruncateChars: 100, ReductionFloor: 0.0);

        var (compacted, report) = await VerbatimCompactor(opts)
            .CompactWithVerbatimFirstAsync(session);

        report.Strategy.Should().Be("verbatim");
        var kept = compacted.Messages.FirstOrDefault(m => m.ToolCallId == "c-long");
        kept.Should().NotBeNull("relevant prose is kept, not dropped");
        kept!.Content.Should().Contain("truncated by verbatim compactor",
            "truncation must leave a visible marker");
        kept.Content.Should().StartWith(longProse[..100]);
    }

    [Fact]
    public async Task Golden_File_Paths_Error_Messages_And_Commands_Survive_Verbatim()
    {
        var golden = "auth middleware redirect loop. Tool result exact content: " +
            "file paths src/OpenMono.Cli/Session/Compactor.cs and src/Auth/Middleware.cs; " +
            "error messages: error CS0161: not all code paths return a value; " +
            "exact commands: git reset --hard HEAD~1.";
        var oldWindow = new List<Message>
        {
            User("auth middleware redirect loop triage"),
            Assistant("auth middleware redirect loop triage"),
            Call("c-gold", "Build", "{\"target\":\"auth\"}"),
            Result("c-gold", "Build", golden),
            User("auth middleware redirect loop follow-up"),
            Assistant("auth middleware redirect loop follow-up"),
        };
        var session = BuildSession(oldWindow, RecentTurnsAboutAuth());

        var (compacted, _) = await VerbatimCompactor(
                new VerbatimCompactionOptions(ReductionFloor: 0.0))
            .CompactWithVerbatimFirstAsync(session);

        var kept = compacted.Messages.FirstOrDefault(m => m.ToolCallId == "c-gold");
        kept.Should().NotBeNull();
        kept!.Content.Should().Be(golden);
        kept.Content.Should().Contain("src/OpenMono.Cli/Session/Compactor.cs")
            .And.Contain("error CS0161")
            .And.Contain("git reset --hard HEAD~1");
    }

    [Fact]
    public async Task Message_Order_Preserved_And_Text_Never_Reordered()
    {
        var fresh = "auth middleware redirect loop tool result exact content file paths error messages exact commands";
        var oldWindow = new List<Message>
        {
            User("aaa first"),
            Call("c-ord", "FileRead", "{\"path\":\"a.txt\"}"),
            Result("c-ord", "FileRead", fresh),
            Assistant("bbb second"),
            User("ccc third"),
            Assistant("ddd fourth"),
        };
        var session = BuildSession(oldWindow, RecentTurnsAboutAuth());

        var (compacted, _) = await VerbatimCompactor(
                new VerbatimCompactionOptions(ReductionFloor: 0.0))
            .CompactWithVerbatimFirstAsync(session);

        var contents = compacted.Messages.Select(m => m.Content ?? $"call:{m.ToolCalls![0].Id}").ToList();
        var ia = contents.IndexOf("aaa first");
        var ic = contents.IndexOf("ccc third");
        var ib = contents.IndexOf("bbb second");
        var id = contents.IndexOf("ddd fourth");
        (ia < ib && ib < ic && ic < id).Should().BeTrue("original relative order is preserved");
    }

    [Fact]
    public async Task Insufficient_Reduction_Falls_Back_To_Summarizing_Compactor()
    {
        // Everything is fresh: short pairs on the current topic plus text.
        // The verbatim pass keeps it all, removes ~nothing, and must defer
        // to the existing summarizer instead of returning a no-op.
        var fresh = "auth middleware redirect loop tool result exact content file paths error messages exact commands";
        var oldWindow = new List<Message>
        {
            User("auth middleware redirect loop one"),
            Assistant("auth middleware redirect loop one"),
            Call("c-a", "FileRead", "{\"path\":\"a.txt\"}"),
            Result("c-a", "FileRead", fresh),
            User("auth middleware redirect loop two"),
            Assistant("auth middleware redirect loop two"),
        };
        var session = BuildSession(oldWindow, RecentTurnsAboutAuth());
        var summaryLlm = new SummaryLlm();
        var compactor = new Compactor(summaryLlm, 100_000);

        var (compacted, report) = await compactor.CompactWithVerbatimFirstAsync(session);

        summaryLlm.Calls.Should().BeGreaterThan(0, "fallback must reach the summarizing compactor");
        report.Strategy.Should().Be("summary");
        report.MessagesCompressed.Should().BeGreaterThan(0);
        compacted.Messages.Should().Contain(m =>
            (m.Content ?? string.Empty).Contains("summary of old turns"));
    }

    [Fact]
    public async Task Opt_Out_Flag_Goes_Straight_To_Summary()
    {
        var staleResult = string.Concat(Enumerable.Repeat("database migration row ", 60));
        var oldWindow = new List<Message>
        {
            User("old database topic"),
            Assistant("old database topic"),
            Call("c-s", "DbQuery", "{}"),
            Result("c-s", "DbQuery", staleResult),
            User("old database topic again"),
            Assistant("old database topic again"),
        };
        var session = BuildSession(oldWindow, RecentTurnsAboutAuth());
        var summaryLlm = new SummaryLlm();
        var compactor = new Compactor(summaryLlm, 100_000,
            verbatimOptions: new VerbatimCompactionOptions(Enabled: false));

        var (_, report) = await compactor.CompactWithVerbatimFirstAsync(session);

        summaryLlm.Calls.Should().BeGreaterThan(0);
        report.Strategy.Should().Be("summary");
    }

    [Fact]
    public async Task Pending_Unanswered_Tool_Call_Survives_Verbatim_Pass()
    {
        var staleResult = string.Concat(Enumerable.Repeat("database migration row ", 60));
        var oldWindow = new List<Message>
        {
            User("old database topic"),
            Assistant("old database topic"),
            Call("c-s", "DbQuery", "{}"),
            Result("c-s", "DbQuery", staleResult),
            User("delete the prod table"),
            Call("call_pending", "DangerousTool", "{}"),
        };
        var session = BuildSession(oldWindow, RecentTurnsAboutAuth());

        var (compacted, report) = await VerbatimCompactor(
                new VerbatimCompactionOptions(ReductionFloor: 0.0))
            .CompactWithVerbatimFirstAsync(session);

        report.Strategy.Should().Be("verbatim");
        compacted.Messages.Should().Contain(m =>
            m.ToolCalls != null && m.ToolCalls.Any(c => c.Id == "call_pending"),
            "an unanswered call must survive even when its topic looks stale — " +
            "resolving the permission later needs this exact message");
    }

    private sealed class SummaryLlm : ILlmClient
    {
        public int Calls { get; private set; }
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages, JsonElement? toolDefs, LlmOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            yield return new StreamChunk { TextDelta = "summary of old turns", IsComplete = true };
            await Task.CompletedTask;
        }
        public void Dispose() { }
    }

    private sealed class FailingLlm : ILlmClient
    {
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages, JsonElement? toolDefs, LlmOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.FromException(new InvalidOperationException("verbatim path must not call the LLM"));
            yield break;
        }
        public void Dispose() { }
    }
}
