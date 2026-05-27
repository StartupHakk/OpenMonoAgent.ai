using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Acp;
using OpenMono.Config;
using OpenMono.Llm;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;
using Xunit;

namespace OpenMono.Tests.Acp;

public sealed class AcpEventSinkTests
{
    [Fact]
    public async Task Null_sink_runs_turn_to_completion_without_error()
    {
        var (loop, session) = BuildLoop(sink: null, llm: new FakeLlm([
            new StreamChunk { TextDelta = "hi", IsComplete = false },
            new StreamChunk { IsComplete = true, Usage = new UsageInfo { PromptTokens = 5, CompletionTokens = 3 } },
        ]));

        Func<Task> act = () => loop.RunTurnAsync("hello", null, CancellationToken.None);
        await act.Should().NotThrowAsync();

        session.Messages.Should().Contain(m => m.Role == MessageRole.Assistant && m.Content == "hi");
    }

    [Fact]
    public async Task Text_delta_events_fire_in_order_with_streamed_content()
    {
        var sink = new RecordingSink();
        var (loop, _) = BuildLoop(sink, new FakeLlm([
            new StreamChunk { TextDelta = "Hel", IsComplete = false },
            new StreamChunk { TextDelta = "lo, ", IsComplete = false },
            new StreamChunk { TextDelta = "world!", IsComplete = false },
            new StreamChunk { IsComplete = true, Usage = new UsageInfo { PromptTokens = 10, CompletionTokens = 5 } },
        ]));

        await loop.RunTurnAsync("hi", null, CancellationToken.None);

        sink.TextDeltas.Should().Equal("Hel", "lo, ", "world!");
    }

    [Fact]
    public async Task Thinking_delta_events_fire_when_llm_emits_thinking_chunks()
    {
        var sink = new RecordingSink();
        var (loop, _) = BuildLoop(sink, new FakeLlm([
            new StreamChunk { ThinkingDelta = "Reasoning...", IsComplete = false },
            new StreamChunk { ThinkingDelta = "more...", IsComplete = false },
            new StreamChunk { TextDelta = "done", IsComplete = false },
            new StreamChunk { IsComplete = true, Usage = new UsageInfo { PromptTokens = 8, CompletionTokens = 4 } },
        ]));

        await loop.RunTurnAsync("explain", null, CancellationToken.None);

        sink.ThinkingDeltas.Should().Equal("Reasoning...", "more...");
    }

    [Fact]
    public async Task Usage_event_fires_once_with_token_tracker_totals_on_text_only_turn()
    {
        var sink = new RecordingSink();
        var session = new SessionState();
        session.AddMessage(new Message { Role = MessageRole.System, Content = "sys" });
        session.Meta.TokenTracker = new TokenTracker();

        var (loop, _) = BuildLoop(sink, new FakeLlm([
            new StreamChunk { TextDelta = "ok", IsComplete = false },
            new StreamChunk { IsComplete = true, Usage = new UsageInfo { PromptTokens = 42, CompletionTokens = 7 } },
        ]), session);

        await loop.RunTurnAsync("hi", null, CancellationToken.None);

        sink.UsageEvents.Should().HaveCount(1);
        sink.UsageEvents[0].input.Should().Be(42);
        sink.UsageEvents[0].output.Should().Be(7);
        sink.UsageEvents[0].total.Should().Be(49);
    }

    [Fact]
    public async Task Tool_result_preview_fires_for_each_executed_tool_with_call_id_and_preview()
    {
        var sink = new RecordingSink();
        var tools = new ToolRegistry();
        tools.Register(new PreviewTool());

        var (loop, _) = BuildLoop(sink, new FakeLlm(
            new List<StreamChunk>
            {
                new() { ToolCallDelta = new ToolCall { Id = "call_42", Name = "PreviewTool", Arguments = "{}" }, IsComplete = false },
                new() { IsComplete = true },
            },
            new List<StreamChunk>
            {
                new() { TextDelta = "all done", IsComplete = false },
                new() { IsComplete = true, Usage = new UsageInfo { PromptTokens = 3, CompletionTokens = 1 } },
            }),
            tools: tools);

        await loop.RunTurnAsync("invoke", null, CancellationToken.None);

        sink.ToolPreviews.Should().HaveCount(1);
        sink.ToolPreviews[0].callId.Should().Be("call_42");
        sink.ToolPreviews[0].preview.Should().Be("preview-payload");
        sink.ToolPreviews[0].artifactId.Should().BeNull();
    }

    [Fact]
    public async Task Tool_start_and_end_fire_around_each_tool_execution()
    {
        var sink = new RecordingSink();
        var tools = new ToolRegistry();
        tools.Register(new PreviewTool());

        var (loop, _) = BuildLoop(sink, new FakeLlm(
            new List<StreamChunk>
            {
                new() { ToolCallDelta = new ToolCall { Id = "call_99", Name = "PreviewTool", Arguments = "{\"file_path\":\"src/foo.ts\"}" }, IsComplete = false },
                new() { IsComplete = true },
            },
            new List<StreamChunk>
            {
                new() { TextDelta = "ok", IsComplete = false },
                new() { IsComplete = true, Usage = new UsageInfo { PromptTokens = 1, CompletionTokens = 1 } },
            }),
            tools: tools);

        await loop.RunTurnAsync("invoke", null, CancellationToken.None);

        sink.ToolStarts.Should().ContainSingle();
        sink.ToolStarts[0].callId.Should().Be("call_99");
        sink.ToolStarts[0].name.Should().Be("PreviewTool");
        sink.ToolStarts[0].summary.Should().Contain("file_path");

        sink.ToolEnds.Should().ContainSingle();
        sink.ToolEnds[0].callId.Should().Be("call_99");
        sink.ToolEnds[0].name.Should().Be("PreviewTool");
        sink.ToolEnds[0].ok.Should().BeTrue("PreviewTool returns ToolResult.Success");
        sink.ToolEnds[0].durationMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Tool_end_reports_ok_false_when_tool_returns_error()
    {
        var sink = new RecordingSink();
        var tools = new ToolRegistry();
        tools.Register(new AlwaysErrorTool());

        var (loop, _) = BuildLoop(sink, new FakeLlm(
            new List<StreamChunk>
            {
                new() { ToolCallDelta = new ToolCall { Id = "call_err", Name = "AlwaysErrorTool", Arguments = "{}" }, IsComplete = false },
                new() { IsComplete = true },
            },
            new List<StreamChunk>
            {
                new() { TextDelta = "noted", IsComplete = false },
                new() { IsComplete = true, Usage = new UsageInfo { PromptTokens = 1, CompletionTokens = 1 } },
            }),
            tools: tools);

        await loop.RunTurnAsync("invoke", null, CancellationToken.None);

        sink.ToolEnds.Should().ContainSingle();
        sink.ToolEnds[0].ok.Should().BeFalse();
    }



    private static (ConversationLoop loop, SessionState session) BuildLoop(
        IAcpEventSink? sink,
        FakeLlm llm,
        SessionState? session = null,
        ToolRegistry? tools = null)
    {
        session ??= new SessionState();
        if (session.Messages.Count == 0)
            session.AddMessage(new Message { Role = MessageRole.System, Content = "sys" });
        tools ??= new ToolRegistry();
        var renderer = new TerminalRenderer();
        var config = new AppConfig();
        var permissions = new PermissionEngine(config, renderer, renderer);
        var loop = new ConversationLoop(
            llm, tools, permissions, renderer, renderer, renderer, config, session,
            sink: sink);
        return (loop, session);
    }

    private sealed class FakeLlm : ILlmClient
    {
        private readonly List<List<StreamChunk>> _rounds;
        private int _i;
        public FakeLlm(params List<StreamChunk>[] rounds) { _rounds = [.. rounds]; }
        public FakeLlm(IEnumerable<StreamChunk> singleRound) { _rounds = [singleRound.ToList()]; }

        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages, JsonElement? toolDefs, LlmOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var chunks = _i < _rounds.Count ? _rounds[_i] : [new StreamChunk { IsComplete = true }];
            _i++;
            foreach (var c in chunks) { yield return c; await Task.Yield(); }
        }

        public void Dispose() { }
    }

    private sealed class RecordingSink : IAcpEventSink
    {
        public List<string> TextDeltas { get; } = new();
        public List<string> ThinkingDeltas { get; } = new();
        public List<(string callId, string name, string summary)> ToolStarts { get; } = new();
        public List<(string callId, string name, bool ok, double durationMs)> ToolEnds { get; } = new();
        public List<(string callId, string preview, string? artifactId)> ToolPreviews { get; } = new();
        public List<(int input, int output, int total)> UsageEvents { get; } = new();
        public List<(int compressed, double seconds, int idx)> Compactions { get; } = new();

        public Task OnTextDeltaAsync(string content) { TextDeltas.Add(content); return Task.CompletedTask; }
        public Task OnThinkingDeltaAsync(string content) { ThinkingDeltas.Add(content); return Task.CompletedTask; }
        public Task OnToolStartAsync(string callId, string name, string summary)
        { ToolStarts.Add((callId, name, summary)); return Task.CompletedTask; }
        public Task OnToolEndAsync(string callId, string name, bool ok, double durationMs)
        { ToolEnds.Add((callId, name, ok, durationMs)); return Task.CompletedTask; }
        public Task OnCompactionAsync(int m, double s, int i) { Compactions.Add((m, s, i)); return Task.CompletedTask; }
        public Task OnUsageAsync(int i, int o, int t) { UsageEvents.Add((i, o, t)); return Task.CompletedTask; }
        public Task OnToolResultPreviewAsync(string callId, string preview, string? artifactId)
        { ToolPreviews.Add((callId, preview, artifactId)); return Task.CompletedTask; }
    }

    private sealed class PreviewTool : ITool
    {
        public string Name => "PreviewTool";
        public string Description => "Returns a fixed preview";
        public bool IsConcurrencySafe => true;
        public bool IsReadOnly => true;
        public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        public PermissionLevel RequiredPermission(JsonElement input) => PermissionLevel.AutoAllow;
        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
            => Task.FromResult(ToolResult.Success("preview-payload"));
    }

    private sealed class AlwaysErrorTool : ITool
    {
        public string Name => "AlwaysErrorTool";
        public string Description => "Returns ToolResult.Error to verify tool_end ok=false";
        public bool IsConcurrencySafe => true;
        public bool IsReadOnly => true;
        public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        public PermissionLevel RequiredPermission(JsonElement input) => PermissionLevel.AutoAllow;
        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
            => Task.FromResult(ToolResult.Error("nope"));
    }
}
