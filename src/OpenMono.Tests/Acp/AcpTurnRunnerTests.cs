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
    public async Task ResumeWithPermissionAsync_allow_once_executes_tool_appends_real_result_and_does_not_cache()
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

        // scope omitted → defaults to "once"
        using var payload = JsonDocument.Parse($"{{\"id\":\"{pauseId}\",\"decision\":\"allow\"}}");
        await runner.ResumeWithPermissionAsync(payload.RootElement, CancellationToken.None);

        // The tool must have actually executed: a real Tool result is appended for the pending call.
        var toolMsg = session.Messages.LastOrDefault(m => m.Role == MessageRole.Tool && m.ToolCallId == "call_p");
        toolMsg.Should().NotBeNull("the approved tool must run on resume, not be left for the LLM to re-issue");
        toolMsg!.Content.Should().Be("done");
        toolMsg.IsError.Should().BeFalse();

        // "once" scope must NOT persist: a later call this session prompts again.
        session.TryGetRememberedPermission(ctx.ContextKey).Should().BeNull(
            "an allow-once decision is temporary and must not be cached for the session");

        ParseSseEvents(body).Last().name.Should().Be("done");
    }

    [Fact]
    public async Task ResumeWithPermissionAsync_allow_session_caches_decision()
    {
        var tools = new ToolRegistry();
        tools.Register(new AskingTool());

        var (runner, session, _) = BuildHarness(
            tools: tools,
            llmRounds: new List<List<StreamChunk>>
            {
                new()
                {
                    new() { ToolCallDelta = new ToolCall { Id = "call_p", Name = "AskingTool", Arguments = "{}" }, IsComplete = false },
                    new() { IsComplete = true },
                },
                new() { new() { TextDelta = "done.", IsComplete = false }, new() { IsComplete = true, Usage = new UsageInfo() } },
            });

        await runner.RunUserMessageAsync("delete it", CancellationToken.None);

        var pauseId = session.PendingIds.Single();
        var ctx = session.LookupPauseContext(pauseId)!.Value;

        using var payload = JsonDocument.Parse($"{{\"id\":\"{pauseId}\",\"decision\":\"allow\",\"scope\":\"session\"}}");
        await runner.ResumeWithPermissionAsync(payload.RootElement, CancellationToken.None);

        var cached = session.TryGetRememberedPermission(ctx.ContextKey);
        cached.Should().NotBeNull("an allow-session decision must persist so the tool is not re-prompted this session");
        cached.Value.Allow.Should().BeTrue();
        cached.Value.Scope.Should().Be("session");
    }

    [Fact]
    public async Task ResumeWithPermissionAsync_deny_appends_error_result_without_executing()
    {
        var tools = new ToolRegistry();
        var asking = new AskingTool();
        tools.Register(asking);

        var (runner, session, _) = BuildHarness(
            tools: tools,
            llmRounds: new List<List<StreamChunk>>
            {
                new()
                {
                    new() { ToolCallDelta = new ToolCall { Id = "call_p", Name = "AskingTool", Arguments = "{}" }, IsComplete = false },
                    new() { IsComplete = true },
                },
                new() { new() { TextDelta = "okay, stopping.", IsComplete = false }, new() { IsComplete = true, Usage = new UsageInfo() } },
            });

        await runner.RunUserMessageAsync("delete it", CancellationToken.None);
        var pauseId = session.PendingIds.Single();

        using var payload = JsonDocument.Parse($"{{\"id\":\"{pauseId}\",\"decision\":\"deny\"}}");
        await runner.ResumeWithPermissionAsync(payload.RootElement, CancellationToken.None);

        var toolMsg = session.Messages.LastOrDefault(m => m.Role == MessageRole.Tool && m.ToolCallId == "call_p");
        toolMsg.Should().NotBeNull("a denied tool call must still be answered, with an error result");
        toolMsg!.IsError.Should().BeTrue("denial must be a structured error, not a success the model can misread");
        toolMsg.Content.Should().ContainEquivalentOf("denied");
        toolMsg.Content.Should().Contain("AskingTool", "the deny result must include the tool context");
        toolMsg.Content.Should().ContainEquivalentOf("how they would like to proceed");
        asking.ExecuteCount.Should().Be(0, "a denied tool must not run");
    }

    [Fact]
    public void New_acp_session_defaults_to_plan_mode()
    {
        var s = new AcpSession
        {
            State = new SessionState { Id = "s", StartedAt = DateTime.UtcNow, Model = "m" }
        };
        s.PlanMode.Should().BeTrue(
            "the extension UI defaults to plan mode and only sends the mode on an explicit toggle, " +
            "so the server must default to plan mode or writes would run while the UI shows 'plan'");
    }

    [Fact]
    public async Task Plan_mode_blocks_write_tool_without_executing_and_returns_error()
    {
        var tools = new ToolRegistry();
        var asking = new AskingTool(); // IsReadOnly == false → a write tool
        tools.Register(asking);

        var session = NewSession();
        session.PlanMode = true; // plan mode active (also the default for real sessions)

        var (runner, _, _) = BuildHarness(session, tools,
            new List<List<StreamChunk>>
            {
                new()
                {
                    new() { ToolCallDelta = new ToolCall { Id = "call_w", Name = "AskingTool", Arguments = "{}" }, IsComplete = false },
                    new() { IsComplete = true },
                },
                new() { new() { TextDelta = "I can't in plan mode.", IsComplete = false }, new() { IsComplete = true, Usage = new UsageInfo() } },
            });

        await runner.RunUserMessageAsync("write a file", CancellationToken.None);

        asking.ExecuteCount.Should().Be(0, "write tools must not run in plan mode");
        var toolMsg = session.Messages.LastOrDefault(m => m.Role == MessageRole.Tool && m.ToolCallId == "call_w");
        toolMsg.Should().NotBeNull();
        toolMsg!.IsError.Should().BeTrue();
        toolMsg.Content.Should().Contain("Plan mode");
    }

    [Fact]
    public async Task ResumeWithPlanDecisionAsync_auto_flips_to_build_and_runs_implementation()
    {
        var (runner, session, body) = BuildHarness(
            tools: new ToolRegistry(),
            llmRounds: new List<List<StreamChunk>>
            {
                new() { new() { TextDelta = "implementing the plan", IsComplete = false }, new() { IsComplete = true, Usage = new UsageInfo() } },
            });
        session.PlanMode = true;

        await runner.ResumeWithPlanDecisionAsync("auto", CancellationToken.None);

        session.PlanMode.Should().BeFalse("Auto implement flips Plan → Build");
        session.AutoApproveWrites.Should().BeTrue("Auto implement pre-approves writes");
        var events = ParseSseEvents(body);
        events.Should().Contain(e => e.name == "mode_changed", "the UI must learn about the flip");
        events.Last().name.Should().Be("done");
    }

    [Fact]
    public async Task ResumeWithPlanDecisionAsync_gated_flips_to_build_without_auto_approving()
    {
        var (runner, session, _) = BuildHarness(
            tools: new ToolRegistry(),
            llmRounds: new List<List<StreamChunk>>
            {
                new() { new() { IsComplete = true, Usage = new UsageInfo() } },
            });
        session.PlanMode = true;

        await runner.ResumeWithPlanDecisionAsync("gated", CancellationToken.None);

        session.PlanMode.Should().BeFalse();
        session.AutoApproveWrites.Should().BeFalse("Ask-before-edits leaves writes going through normal prompts");
    }

    [Fact]
    public async Task ResumeWithPlanDecisionAsync_keep_stays_in_plan_mode()
    {
        var (runner, session, body) = BuildHarness(new ToolRegistry(), new());
        session.PlanMode = true;

        await runner.ResumeWithPlanDecisionAsync("keep", CancellationToken.None);

        session.PlanMode.Should().BeTrue("Keep planning must not switch modes");
        ParseSseEvents(body).Should().Contain(e => e.name == "done");
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
    public async Task ResumeWithUserInputAsync_caches_answer_and_appends_synthetic_tool_message()
    {
        var session = NewSession();

        session.Messages.Add(new Message { Role = MessageRole.User, Content = "ask me" });
        session.Messages.Add(new Message
        {
            Role = MessageRole.Assistant,
            ToolCalls = new() { new ToolCall { Id = "call_ask", Name = "AskUser", Arguments = "{\"question\":\"which?\"}" } },
        });
        session.RegisterPause("ask_42", PendingResponseKind.UserInput, "which?");

        var (runner, _, _) = BuildHarness(session, new ToolRegistry(),
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
            State = new SessionState
            {
                Id = "sess_" + Guid.NewGuid().ToString("N")[..8],
                StartedAt = DateTime.UtcNow,
                Model = "test-model",
            },
        };
        s.Messages.Add(new Message { Role = MessageRole.System, Content = "you are helpful" });
        // These harness tests exercise build-mode permission flow; opt out of the
        // plan-mode default (read-only) so write tools reach the permission engine.
        s.PlanMode = false;
        return s;
    }

    [Fact]
    public async Task ResumeWithPermissionAsync_scope_session_caches_permission_persistently()
    {
        var tools = new ToolRegistry();
        tools.Register(new AskingTool());

        var (runner, session, _) = BuildHarness(
            tools: tools,
            llmRounds: new List<List<StreamChunk>>
            {
                new()
                {
                    new() { ToolCallDelta = new ToolCall { Id = "call_1", Name = "AskingTool", Arguments = "{}" }, IsComplete = false },
                    new() { IsComplete = true },
                },
            });

        await runner.RunUserMessageAsync("request 1", CancellationToken.None);

        var pauseId = session.PendingIds.Single();
        var ctx = session.LookupPauseContext(pauseId)!.Value;

        // Approve with scope="session"
        using var payload = JsonDocument.Parse($"{{\"id\":\"{pauseId}\",\"decision\":\"allow\",\"scope\":\"session\"}}");
        await runner.ResumeWithPermissionAsync(payload.RootElement, CancellationToken.None);

        // Verify permission is cached with session scope
        var cached = session.TryGetRememberedPermission(ctx.ContextKey);
        cached.Should().NotBeNull();
        cached.Value.Allow.Should().BeTrue();
        cached.Value.Scope.Should().Be("session");
    }

    [Fact]
    public async Task ResumeWithPermissionAsync_scope_once_forgets_permission_after_execution()
    {
        var tools = new ToolRegistry();
        tools.Register(new AskingTool());

        var (runner, session, _) = BuildHarness(
            tools: tools,
            llmRounds: new List<List<StreamChunk>>
            {
                new()
                {
                    new() { ToolCallDelta = new ToolCall { Id = "call_1", Name = "AskingTool", Arguments = "{}" }, IsComplete = false },
                    new() { IsComplete = true },
                },
                new()
                {
                    new() { TextDelta = "done.", IsComplete = false },
                    new() { IsComplete = true, Usage = new UsageInfo() },
                },
            });

        await runner.RunUserMessageAsync("request 1", CancellationToken.None);

        var pauseId = session.PendingIds.Single();
        var ctx = session.LookupPauseContext(pauseId)!.Value;

        // Approve with scope="once"
        using var payload = JsonDocument.Parse($"{{\"id\":\"{pauseId}\",\"decision\":\"allow\",\"scope\":\"once\"}}");
        await runner.ResumeWithPermissionAsync(payload.RootElement, CancellationToken.None);

        // Verify permission is not cached (forgotten after execution)
        var cached = session.TryGetRememberedPermission(ctx.ContextKey);
        cached.Should().BeNull("scope=once should be forgotten after execution");
    }

    [Fact]
    public async Task ResumeWithPermissionAsync_after_compaction_still_executes_the_real_tool()
    {
        var tools = new ToolRegistry();
        tools.Register(new AskingTool());

        static List<StreamChunk> TextRound(string text) =>
        [
            new() { TextDelta = text, IsComplete = false },
            new() { IsComplete = true, Usage = new UsageInfo() },
        ];

        var (runner, session, _) = BuildHarness(
            tools: tools,
            llmRounds: new List<List<StreamChunk>>
            {
                // Fake LLM rounds are consumed in call order, not matched to message content —
                // the tool-call round must be third to line up with the "delete it" turn below.
                TextRound("chat reply 1"),
                TextRound("chat reply 2"),
                new()
                {
                    new() { ToolCallDelta = new ToolCall { Id = "call_p", Name = "AskingTool", Arguments = "{}" }, IsComplete = false },
                    new() { IsComplete = true },
                },
                TextRound("ok 1"), TextRound("ok 2"), TextRound("ok 3"), TextRound("ok 4"), TextRound("ok 5"),
            });

        // Two ordinary turns of chat, then the risky call that pauses for a permission decision.
        await runner.RunUserMessageAsync("chat 1", CancellationToken.None);
        await runner.RunUserMessageAsync("chat 2", CancellationToken.None);
        await runner.RunUserMessageAsync("delete it", CancellationToken.None);
        var pauseId = session.PendingIds.Single();

        // Five more turns pass before the user gets back to that permission prompt — the ACP
        // layer explicitly supports outstanding/queued permissions, so this is a real scenario.
        for (var i = 0; i < 5; i++)
            await runner.RunUserMessageAsync($"meanwhile {i}", CancellationToken.None);

        // A long-running session eventually compacts. Simulate that happening while the
        // permission is still outstanding, using the exact same Compactor the real loop
        // constructs internally (same class, same defaults — just invoked directly here so
        // the test doesn't have to fight token-threshold timing).
        var compactor = new Compactor(new CompactionSummaryLlm(), contextSize: 100_000);
        var (compacted, report) = await compactor.CompactAsync(session.State);
        report.MessagesCompressed.Should().BeGreaterThan(0, "the two 'chat' turns should have actually been summarized");
        session.State.Messages.Clear();
        foreach (var msg in compacted.Messages)
            session.State.AddMessage(msg);

        // Finally resolve the permission that's been pending this whole time.
        using var payload = JsonDocument.Parse($"{{\"id\":\"{pauseId}\",\"decision\":\"allow\"}}");
        await runner.ResumeWithPermissionAsync(payload.RootElement, CancellationToken.None);

        var toolMsg = session.Messages.LastOrDefault(m => m.Role == MessageRole.Tool && m.ToolCallId == "call_p");
        toolMsg.Should().NotBeNull(
            "the approved tool must still run even though compaction happened while the permission " +
            "was outstanding — before the fix this was silently dropped (ResolvePendingToolCallsAsync " +
            "found no matching assistant tool call and just returned)");
        toolMsg!.Content.Should().Be("done");
        toolMsg.IsError.Should().BeFalse();
    }

    private sealed class CompactionSummaryLlm : ILlmClient
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

    [Fact]
    public void AcpSession_PermissionQueue_serializes_concurrent_permissions()
    {
        var session = NewSession();

        // Enqueue 3 permissions
        var result1 = session.TryEnqueuePermission("perm_1", "FileWrite", "file.ts", false);
        var result2 = session.TryEnqueuePermission("perm_2", "FileWrite", "file.ts", false);
        var result3 = session.TryEnqueuePermission("perm_3", "FileWrite", "file.ts", false);

        // First should process, others queued
        result1.Should().BeTrue("first permission should process immediately");
        result2.Should().BeFalse("second permission should be queued");
        result3.Should().BeFalse("third permission should be queued");
    }

    [Fact]
    public void AcpSession_PermissionQueue_dequeues_in_order()
    {
        var session = NewSession();

        // Enqueue 3 permissions
        session.TryEnqueuePermission("perm_1", "FileWrite", "file.ts", false);
        session.TryEnqueuePermission("perm_2", "FileWrite", "file.ts", false);
        session.TryEnqueuePermission("perm_3", "FileWrite", "file.ts", false);

        // Dequeue them
        var next2 = session.DequeueNextPermission();
        next2.Should().NotBeNull();
        next2.Value.Id.Should().Be("perm_2");

        var next3 = session.DequeueNextPermission();
        next3.Should().NotBeNull();
        next3.Value.Id.Should().Be("perm_3");

        var next4 = session.DequeueNextPermission();
        next4.Should().BeNull("queue should be empty");
    }

    [Fact]
    public void AcpSession_PermissionQueue_isolated_per_session()
    {
        var session1 = NewSession();
        var session2 = NewSession();

        // Enqueue in session 1
        session1.TryEnqueuePermission("perm_1", "FileWrite", "file.ts", false);
        session1.TryEnqueuePermission("perm_2", "FileWrite", "file.ts", false);

        // Enqueue in session 2 (both, so one is in-flight and one is queued)
        session2.TryEnqueuePermission("perm_3", "WebFetch", "url", false);
        session2.TryEnqueuePermission("perm_4", "WebFetch", "url", false);

        // Verify independence: session1's queue has perm_2, session2's queue has perm_4
        var next1 = session1.DequeueNextPermission();
        next1.Should().NotBeNull();
        next1!.Value.Id.Should().Be("perm_2");

        var next2 = session2.DequeueNextPermission();
        next2.Should().NotBeNull();
        next2!.Value.Id.Should().Be("perm_4", "session2's queue should contain perm_4 (perm_3 was in-flight)");

        // Verify both queues are now empty
        session1.DequeueNextPermission().Should().BeNull();
        session2.DequeueNextPermission().Should().BeNull();
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




    [Fact]
    public async Task Pause_persists_completed_sibling_results_so_resume_never_reexecutes_them()
    {
        var tools = new ToolRegistry();
        var counting = new CountingTool();
        var asking = new AskingTool();
        tools.Register(counting);
        tools.Register(asking);

        // One assistant message, two sequential (writeable) calls: the first auto-executes,
        // the second pauses for permission mid-batch.
        var (runner, session, _) = BuildHarness(
            tools: tools,
            llmRounds: new List<List<StreamChunk>>
            {
                new()
                {
                    new() { ToolCallDelta = new ToolCall { Id = "call_a", Name = "CountingTool", Arguments = "{}" }, IsComplete = false },
                    new() { ToolCallDelta = new ToolCall { Id = "call_b", Name = "AskingTool", Arguments = "{}" }, IsComplete = false },
                    new() { IsComplete = true },
                },
                new() { new() { TextDelta = "done.", IsComplete = false }, new() { IsComplete = true, Usage = new UsageInfo() } },
            });

        await runner.RunUserMessageAsync("run both", CancellationToken.None);

        counting.ExecuteCount.Should().Be(1);
        session.Messages.Should().Contain(m => m.Role == MessageRole.Tool && m.ToolCallId == "call_a",
            "the completed first call must be persisted when the batch pauses, or the resume re-executes it");

        var pauseId = session.PendingIds.Single();
        using var payload = JsonDocument.Parse($"{{\"id\":\"{pauseId}\",\"decision\":\"allow\"}}");
        await runner.ResumeWithPermissionAsync(payload.RootElement, CancellationToken.None);

        counting.ExecuteCount.Should().Be(1, "an already-executed write must never run twice across a pause");
        asking.ExecuteCount.Should().Be(1);
        session.Messages.Count(m => m.Role == MessageRole.Tool && m.ToolCallId == "call_a").Should().Be(1);
        session.Messages.Count(m => m.Role == MessageRole.Tool && m.ToolCallId == "call_b").Should().Be(1);
    }

    [Fact]
    public async Task Mode_banner_is_ephemeral_and_never_persisted_into_the_session()
    {
        var tools = new ToolRegistry();
        tools.Register(new CountingTool());

        // Two LLM rounds in one turn: tool call, then final text — the banner is
        // prepended per iteration and must not accumulate in the stored system message.
        var (runner, session, _) = BuildHarness(
            tools: tools,
            llmRounds: new List<List<StreamChunk>>
            {
                new()
                {
                    new() { ToolCallDelta = new ToolCall { Id = "call_1", Name = "CountingTool", Arguments = "{}" }, IsComplete = false },
                    new() { IsComplete = true },
                },
                new() { new() { TextDelta = "done.", IsComplete = false }, new() { IsComplete = true, Usage = new UsageInfo() } },
            });

        await runner.RunUserMessageAsync("go", CancellationToken.None);

        var system = session.Messages.First(m => m.Role == MessageRole.System);
        system.Content.Should().NotContain("ACTIVE MODE",
            "the per-turn mode banner must stay ephemeral; persisting it stacks a copy per LLM call and busts the prompt cache");
    }

    private sealed class CountingTool : ITool
    {
        public string Name => "CountingTool";
        public string Description => "Executes without prompting and counts invocations";
        public bool IsConcurrencySafe => false;
        public bool IsReadOnly => false;
        public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        public PermissionLevel RequiredPermission(JsonElement input) => PermissionLevel.AutoAllow;
        public int ExecuteCount { get; private set; }
        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
        {
            ExecuteCount++;
            return Task.FromResult(ToolResult.Success("counted"));
        }
    }

    private sealed class AskingTool : ITool
    {
        public string Name => "AskingTool";
        public string Description => "Always asks for permission, then succeeds";
        public bool IsConcurrencySafe => false;
        public bool IsReadOnly => false;
        public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
        public PermissionLevel RequiredPermission(JsonElement input) => PermissionLevel.Ask;
        public int ExecuteCount { get; private set; }
        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
        {
            ExecuteCount++;
            return Task.FromResult(ToolResult.Success("done"));
        }
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
