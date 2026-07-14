using System.Runtime.CompilerServices;
using System.Text;
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

public sealed class AcpTurnRunnerTests
{
    [Fact]
    public async Task RunUserMessageAsync_text_only_turn_streams_to_done()
    {
        var (runner, _, body) = BuildHarness(
            tools: new ToolRegistry(),
            llmRounds: new List<List<StreamChunk>>
            {
                new()
                {
                    new() { TextDelta = "Hello", IsComplete = false },
                    new() { IsComplete = true, Usage = new UsageInfo { PromptTokens = 1, CompletionTokens = 1 } },
                }
            });

        await runner.RunUserMessageAsync("hi", CancellationToken.None);

        var events = ParseSseEvents(body);
        events.Should().Contain(e => e.name == "text_delta");
        events.Should().Contain(e => e.name == "done");
    }

    [Fact]
    public async Task Tool_requiring_permission_emits_permission_request_and_pauses()
    {
        var tools = new ToolRegistry();
        tools.Register(new AskingTool());

        var (runner, session, body) = BuildHarness(
            tools: tools,
            llmRounds: new List<List<StreamChunk>>
            {
                new()
                {
                    new() { ToolCallDelta = new ToolCall { Id = "call_p", Name = "AskingTool", Arguments = "{}" }, IsComplete = false },
                    new() { IsComplete = true, Usage = new UsageInfo() },
                },
            });

        await runner.RunUserMessageAsync("do it", CancellationToken.None);

        var events = ParseSseEvents(body);
        events.Should().Contain(e => e.name == "permission_request");
        events.Should().NotContain(e => e.name == "done", "the turn paused, the stream must close without `done`");

        session.PendingIds.Should().HaveCount(1);
        var pauseId = session.PendingIds.Single();
        session.LookupPauseContext(pauseId)!.Value.Kind.Should().Be(PendingResponseKind.Permission);
    }

    [Fact]
    public async Task ResumeWithPermissionAsync_allow_caches_decision_and_completes_turn()
    {
        var tools = new ToolRegistry();
        tools.Register(new AskingTool());



        var (runner, session, body) = BuildHarness(
            tools: tools,
            llmRounds: new List<List<StreamChunk>>
            {
                new()
                {
                    new() { ToolCallDelta = new ToolCall { Id = "call_p", Name = "AskingTool", Arguments = "{}" }, IsComplete = false },
                    new() { IsComplete = true },
                },
                new()
                {
                    new() { TextDelta = "done.", IsComplete = false },
                    new() { IsComplete = true, Usage = new UsageInfo() },
                },
            });

        await runner.RunUserMessageAsync("delete it", CancellationToken.None);

        var pauseId = session.PendingIds.Single();
        var ctx = session.LookupPauseContext(pauseId)!.Value;

        using var payload = JsonDocument.Parse($"{{\"id\":\"{pauseId}\",\"decision\":\"allow\"}}");
        await runner.ResumeWithPermissionAsync(payload.RootElement, CancellationToken.None);

        session.TryGetRememberedPermission(ctx.ContextKey).Should().BeTrue(
            "the allow decision must persist in the session cache so the loop can re-run the call without re-pausing");

        var toolMsg = session.Messages.LastOrDefault(m => m.Role == MessageRole.Tool);
        toolMsg.Should().NotBeNull("allow must execute the pending tool call, not ask the model to re-issue it");
        toolMsg!.ToolCallId.Should().Be("call_p");
        toolMsg.Content.Should().Be("done");

        var events = ParseSseEvents(body);
        events.Should().Contain(e => e.name == "tool_start");
        events.Last().name.Should().Be("done");
    }

    [Fact]
    public async Task ResumeWithPermissionAsync_with_unknown_id_emits_error_event()
    {
        var (runner, _, body) = BuildHarness(
            tools: new ToolRegistry(),
            llmRounds: new List<List<StreamChunk>>
            {
                new() { new() { TextDelta = "noop", IsComplete = false }, new() { IsComplete = true } },
            });

        using var payload = JsonDocument.Parse("{\"id\":\"perm_ghost\",\"decision\":\"allow\"}");

        Func<Task> act = () => runner.ResumeWithPermissionAsync(payload.RootElement, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*unknown or already-resolved pause id*");
    }

    [Fact]
    public async Task ResumeWithPermissionAsync_rejects_kind_mismatch()
    {

        var tools = new ToolRegistry();
        var (runner, session, _) = BuildHarness(tools, new());

        session.RegisterPause("ask_1", PendingResponseKind.UserInput, "what?");

        using var payload = JsonDocument.Parse("{\"id\":\"ask_1\",\"decision\":\"allow\"}");

        Func<Task> act = () => runner.ResumeWithPermissionAsync(payload.RootElement, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not a Permission pause*");
    }

    [Fact]
    public async Task ResumeWithUserInputAsync_caches_answer_and_reexecutes_pending_tool_call()
    {
        var session = NewSession();

        session.Messages.Add(new Message { Role = MessageRole.User, Content = "ask me" });
        session.Messages.Add(new Message
        {
            Role = MessageRole.Assistant,
            ToolCalls = new() { new ToolCall { Id = "call_ask", Name = "AskUser", Arguments = "{\"question\":\"which?\"}" } },
        });
        session.RegisterPause("ask_42", PendingResponseKind.UserInput, "which?");

        var tools = new ToolRegistry();
        tools.Register(new AskUserTool());
        var (runner, _, _) = BuildHarness(session, tools,
            new List<List<StreamChunk>>
            {
                new() { new() { TextDelta = "ok", IsComplete = false }, new() { IsComplete = true } },
            });

        using var payload = JsonDocument.Parse("{\"id\":\"ask_42\",\"value\":\"AES-256-GCM\"}");
        await runner.ResumeWithUserInputAsync(payload.RootElement, CancellationToken.None);

        session.TryGetRememberedUserInput("which?").Should().Be("AES-256-GCM");


        var toolMsg = session.Messages.LastOrDefault(m => m.Role == MessageRole.Tool);
        toolMsg.Should().NotBeNull();
        toolMsg!.ToolCallId.Should().Be("call_ask");
        toolMsg.Content.Should().Be("AES-256-GCM");
    }

    [Fact]
    public void AbortPendingPauses_cancels_outstanding_pauses()
    {
        var (runner, session, _) = BuildHarness(new ToolRegistry(), new());

        var tcs1 = session.RegisterPause("perm_1", PendingResponseKind.Permission, "Bash|x");
        var tcs2 = session.RegisterPause("ask_1", PendingResponseKind.UserInput, "?");

        runner.AbortPendingPauses();

        tcs1.Task.IsCanceled.Should().BeTrue();
        tcs2.Task.IsCanceled.Should().BeTrue();
        session.PendingIds.Should().BeEmpty();
    }



    private static (AcpTurnRunner runner, AcpSession session, MemoryStream body) BuildHarness(
        ToolRegistry tools,
        List<List<StreamChunk>> llmRounds)
    {
        return BuildHarness(NewSession(), tools, llmRounds);
    }

    private static (AcpTurnRunner runner, AcpSession session, MemoryStream body) BuildHarness(
        AcpSession session,
        ToolRegistry tools,
        List<List<StreamChunk>> llmRounds)
    {
        var body = new MemoryStream();
        var writer = new SseWriter(body, CancellationToken.None);
        var config = new AppConfig { DataDirectory = Path.Combine(Path.GetTempPath(), "openmono-runner-" + Guid.NewGuid().ToString("N")[..8]) };
        Directory.CreateDirectory(config.DataDirectory);
        var renderer = new TerminalRenderer();
        var llm = new ScriptedLlm(llmRounds);
        var factory = new ConversationLoopFactory(llm, tools, config, renderer, renderer, renderer);
        var settings = new AcpServerSettings { PendingUserResponseTimeoutMinutes = 1 };
        var runner = new AcpTurnRunner(session, writer, factory, settings);
        return (runner, session, body);
    }

    private static AcpSession NewSession()
    {
        var s = new AcpSession
        {
            Id = "sess_" + Guid.NewGuid().ToString("N")[..8],
            StartedAt = DateTime.UtcNow,
            Model = "test-model",
        };
        s.Messages.Add(new Message { Role = MessageRole.System, Content = "you are helpful" });
        return s;
    }

    private static List<(string name, JsonElement data)> ParseSseEvents(MemoryStream body)
    {
        var text = Encoding.UTF8.GetString(body.ToArray());
        var blocks = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var result = new List<(string, JsonElement)>();
        foreach (var block in blocks)
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2) continue;
            if (!lines[0].StartsWith("event: ") || !lines[1].StartsWith("data: ")) continue;
            var name = lines[0]["event: ".Length..].Trim();
            var data = JsonDocument.Parse(lines[1]["data: ".Length..].Trim()).RootElement.Clone();
            result.Add((name, data));
        }
        return result;
    }




    private sealed class AskingTool : ITool
    {
        public string Name => "AskingTool";
        public string Description => "Always asks for permission, then succeeds";
        public bool IsConcurrencySafe => false;
        public bool IsReadOnly => false;
        public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        public PermissionLevel RequiredPermission(JsonElement input) => PermissionLevel.Ask;
        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
            => Task.FromResult(ToolResult.Success("done"));
    }





    private sealed class ScriptedLlm : ILlmClient
    {
        private readonly List<List<StreamChunk>> _rounds;
        private int _i;
        public ScriptedLlm(List<List<StreamChunk>> rounds) { _rounds = rounds; }

        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages, JsonElement? toolDefs, LlmOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var chunks = _i < _rounds.Count ? _rounds[_i] : new List<StreamChunk> { new() { IsComplete = true } };
            _i++;
            foreach (var c in chunks) { yield return c; await Task.Yield(); }
        }

        public void Dispose() { }
    }
}
