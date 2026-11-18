using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Acp;
using OpenMono.Config;
using OpenMono.HostBridge;
using OpenMono.Llm;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tests.HostBridge;
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
    public async Task RunUserMessageAsync_with_image_parts_and_vision_disabled_emits_warning_and_blocks_turn()
    {
        var (runner, session, body) = BuildHarness(
            tools: new ToolRegistry(),
            llmRounds: new List<List<StreamChunk>>
            {
                new() { new() { TextDelta = "Hello", IsComplete = false }, new() { IsComplete = true } },
            },
            visionEnabled: false);

        var imageParts = new List<ContentPart>
        {
            new ImagePart("data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII="),
        };

        await runner.RunUserMessageAsync("describe this", imageParts, CancellationToken.None);

        // Vision gate: the turn must NOT run (no LLM text_delta from the model),
        // instead a warning text_delta + done is emitted.
        var events = ParseSseEvents(body);
        events.Should().Contain(e => e.name == "text_delta");
        events.Should().Contain(e => e.name == "done");
        // The last text_delta should be the vision warning, not the scripted model reply.
        var lastDelta = events.Last(e => e.name == "text_delta");
        lastDelta.data.GetProperty("content").GetString().Should().Contain("vision");

        // Turn must not have advanced (blocked before DriveLoopAsync).
        session.TurnCount.Should().Be(0, "vision disabled blocks the turn; no turn counted");

        // The user message is still recorded with a placeholder and the image parts.
        var userMsg = session.Messages.Last(m => m.Role == MessageRole.User);
        userMsg.Content.Should().Contain("[1 image(s)]");
        userMsg.ContentParts.Should().HaveCount(2);
        userMsg.ContentParts.Should().ContainSingle(p => p is ImagePart);
    }

    [Fact]
    public async Task RunUserMessageAsync_with_image_parts_and_vision_enabled_runs_the_turn()
    {
        var (runner, session, body) = BuildHarness(
            tools: new ToolRegistry(),
            llmRounds: new List<List<StreamChunk>>
            {
                new() { new() { TextDelta = "Hello", IsComplete = false }, new() { IsComplete = true, Usage = new UsageInfo { PromptTokens = 1, CompletionTokens = 1 } } },
            },
            visionEnabled: true);

        var imageParts = new List<ContentPart>
        {
            new ImagePart("data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII="),
        };

        await runner.RunUserMessageAsync("describe this", imageParts, CancellationToken.None);

        var events = ParseSseEvents(body);
        events.Should().Contain(e => e.name == "text_delta");
        events.Should().Contain(e => e.name == "done");

        // Vision enabled: the actual model turn ran.
        session.TurnCount.Should().BeGreaterThanOrEqualTo(1, "vision enabled allows the turn to run");

        var userMsg = session.Messages.Last(m => m.Role == MessageRole.User);
        userMsg.ContentParts.Should().HaveCount(2);
        userMsg.ContentParts.Should().ContainSingle(p => p is TextPart);
        userMsg.ContentParts.Should().ContainSingle(p => p is ImagePart);
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
        session.Store.TryResolve(ctx.ContextKey).Should().BeNull(
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

        var cached = session.Store.TryResolve(ctx.ContextKey);
        cached.Should().NotBeNull("an allow-session decision must persist so the tool is not re-prompted this session");
        cached.Allow.Should().BeTrue();
        cached.Scope.Should().Be(OpenMono.Permissions.GrantScope.Session);
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
        // The plan-mode default is applied where real sessions are created —
        // AcpSessionStore.Create — so exercise that path rather than a bare AcpSession.
        var tempDir = Path.Combine(Path.GetTempPath(), "openmono-planmode-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            var cfg = new AppConfig { DataDirectory = tempDir };
            cfg.Llm.Model = "test-model";
            var settings = new AcpServerSettings { SessionTtlHours = 24 };
            using var store = new AcpSessionStore(cfg, settings, startReaper: false);

            var s = store.Create(model: "m", cfg);

            s.PlanMode.Should().BeTrue(
                "the extension UI defaults to plan mode and only sends the mode on an explicit toggle, " +
                "so the server must default to plan mode or writes would run while the UI shows 'plan'");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
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

        await runner.ResumeWithPlanDecisionAsync("auto", null, CancellationToken.None);

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

        await runner.ResumeWithPlanDecisionAsync("gated", null, CancellationToken.None);

        session.PlanMode.Should().BeFalse();
        session.AutoApproveWrites.Should().BeFalse("Ask-before-edits leaves writes going through normal prompts");
    }

    [Fact]
    public async Task ResumeWithPlanDecisionAsync_stamps_matching_plan_status_and_time()
    {
        var (runner, session, _) = BuildHarness(
            tools: new ToolRegistry(),
            llmRounds: new List<List<StreamChunk>>
            {
                new() { new() { IsComplete = true, Usage = new UsageInfo() } },
            });
        session.PlanMode = true;
        session.State.Meta.Plans.Add(new PlanRecord { Id = "plan_abc123", Content = "do it", CreatedAtUtc = DateTime.UtcNow });
        session.State.Meta.LastPlanId = "plan_abc123";

        await runner.ResumeWithPlanDecisionAsync("auto", "plan_abc123", CancellationToken.None);

        var plan = session.State.Meta.Plans.Single();
        plan.Status.Should().Be(PlanStatus.Completed, "a clean implementing turn ends Implementing → Completed");
        plan.DecidedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ResumeWithPlanDecisionAsync_leaves_plan_open_when_turn_pauses()
    {
        // The LLM asks for a permission during the implementing turn: the plan
        // must stay Implementing so a later clean turn can complete it.
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
        session.PlanMode = true;
        session.State.Meta.Plans.Add(new PlanRecord { Id = "plan_p", Content = "do it", CreatedAtUtc = DateTime.UtcNow });
        session.State.Meta.LastPlanId = "plan_p";

        await runner.ResumeWithPlanDecisionAsync("gated", "plan_p", CancellationToken.None);

        session.State.Meta.Plans.Single().Status.Should().Be(PlanStatus.Implementing, "the implementing turn paused on a permission — not finished");
        ParseSseEvents(body).Should().Contain(e => e.name == "permission_request");
    }

    [Fact]
    public async Task ResumeWithPlanDecisionAsync_rejects_stale_plan_id()
    {
        var (runner, session, body) = BuildHarness(new ToolRegistry(), new());
        session.PlanMode = true;
        session.State.Meta.Plans.Add(new PlanRecord { Id = "plan_old", Content = "old", Status = PlanStatus.Superseded, CreatedAtUtc = DateTime.UtcNow });
        session.State.Meta.Plans.Add(new PlanRecord { Id = "plan_new", Content = "new", CreatedAtUtc = DateTime.UtcNow });
        session.State.Meta.LastPlanId = "plan_new";

        await runner.ResumeWithPlanDecisionAsync("auto", "plan_old", CancellationToken.None);

        session.PlanMode.Should().BeTrue("a decision for a superseded plan must not switch modes");
        session.State.Meta.Plans.Single(p => p.Id == "plan_new").Status.Should().Be(PlanStatus.Pending);
        ParseSseEvents(body).Should().Contain(e => e.name == "done");
    }

    [Fact]
    public async Task ResumeWithPlanDecisionAsync_keep_stays_in_plan_mode()
    {
        var (runner, session, body) = BuildHarness(new ToolRegistry(), new());
        session.PlanMode = true;

        await runner.ResumeWithPlanDecisionAsync("keep", null, CancellationToken.None);

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

        // No general user-input caching: the answer resolves the pause but is not remembered.
        session.Store.TryResolve("which?").Should().BeNull();


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



    // ── Sudo elevation: ELEVATED permission pause ──────────────────────
    //
    // A root HostExec permission pause carries the password channel: an allow
    // response includes the user's sudo password, which the resume branch
    // verifies against the host daemon BEFORE the pause resolves. A wrong
    // password re-enters the SAME pause (permission_attempt event, pause stays
    // pending, conversation loop stays suspended); only success, the 3rd
    // failure, or a denial resolves it. The daemon is a fake HTTP server: real
    // sudo+password is not deterministically testable in CI.

    private const string ElevatedHostExecArgs = "{\"command\":\"echo as-root-ok\",\"as_root\":true}";

    private static ToolRegistry HostExecTools()
    {
        var tools = new ToolRegistry();
        tools.Register(new OpenMono.Tools.HostExecTool());
        return tools;
    }

    private static void SeedElevatedHostExecCall(AcpSession session)
    {
        // The HostExec tool is only offered while the session is paired with a
        // host daemon — same gate the real /host on flow sets.
        session.State.Meta.HostEnabled = true;
        session.Messages.Add(new Message { Role = MessageRole.User, Content = "run it as root" });
        session.Messages.Add(new Message
        {
            Role = MessageRole.Assistant,
            ToolCalls = new() { new ToolCall { Id = "call_host", Name = "HostExec", Arguments = ElevatedHostExecArgs } },
        });
    }

    private static void RegisterElevatedPause(AcpSession session, string id)
    {
        session.RegisterPause(id, PendingResponseKind.Permission, "HostExec");
        session.MarkPauseElevated(id);
    }

    private static JsonDocument PermissionPayload(
        string id, string decision, string? password = null, string scope = "once")
        => JsonDocument.Parse(JsonSerializer.Serialize(new { id, decision, scope, password }));

    [Fact]
    public async Task Resume_elevated_correctPassword_verifies_then_runs_the_real_command()
    {
        using var daemon = new FakeHostDaemon("correct-horse");
        var (runner, session, body) = BuildHarnessWithGateway("sess_sudo_1", daemon, HostExecTools());
        SeedElevatedHostExecCall(session);
        RegisterElevatedPause(session, "perm_sudo");

        using var payload = PermissionPayload("perm_sudo", "allow", "correct-horse");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await runner.ResumeWithPermissionAsync(payload.RootElement, cts.Token); }
        catch (OperationCanceledException) { throw new Exception("HANG-REPRO: " + System.Environment.StackTrace); }

        session.PendingIds.Should().BeEmpty();
        daemon.VerifyPasswords.Should().Equal(new[] { "correct-horse" },
            "the password is verified with the benign sudo probe BEFORE the real exec");
        daemon.ExecCount.Should().Be(1, "the real command runs once, with the verified password");
        daemon.LastExecPassword.Should().Be("correct-horse");
        var toolMsg = session.Messages.Last(m => m.Role == MessageRole.Tool);
        toolMsg.Content.Should().Contain("root-output");
        var events = ParseSseEvents(body);
        var ok = events.Single(e => e.name == "permission_attempt");
        ok.data.GetProperty("ok").GetBoolean().Should().BeTrue(
            "a verified password emits an ok:true attempt event so the webview can show success");
        ok.data.GetProperty("id").GetString().Should().Be("perm_sudo");
        events.Last().name.Should().Be("done");
    }

    [Fact]
    public async Task Resume_elevated_wrongPassword_keepsPausePending_and_emits_attempt()
    {
        using var daemon = new FakeHostDaemon("correct-horse");
        var (runner, session, body) = BuildHarnessWithGateway("sess_sudo_2", daemon, HostExecTools());
        SeedElevatedHostExecCall(session);
        RegisterElevatedPause(session, "perm_sudo");

        var activityBefore = session.LastActivityAt;
        using var payload = PermissionPayload("perm_sudo", "allow", "wrong-one");
        await runner.ResumeWithPermissionAsync(payload.RootElement, CancellationToken.None);

        // Pause stays pending: the modal/card stays up, no card churn, and the
        // conversation loop was never resumed.
        session.PendingIds.Should().ContainSingle().Which.Should().Be("perm_sudo");
        session.LookupSudoAttemptState("perm_sudo")!.Value.SudoAttempt.Should().Be(1);
        daemon.VerifyPasswords.Should().Equal(new[] { "wrong-one" });
        daemon.ExecCount.Should().Be(0, "a wrong password must never run the command");

        var events = ParseSseEvents(body);
        var attempt = events.Single(e => e.name == "permission_attempt");
        attempt.data.GetProperty("id").GetString().Should().Be("perm_sudo");
        attempt.data.GetProperty("ok").GetBoolean().Should().BeFalse();
        attempt.data.GetProperty("attempt").GetInt32().Should().Be(1);
        attempt.data.GetProperty("max").GetInt32().Should().Be(3);
        events.Should().NotContain(e => e.name == "done");

        // The in-flight attempt refreshed the pending-timeout clock so an
        // actively-typed modal is never GC-swept.
        session.LastActivityAt.Should().BeAfter(activityBefore);
    }

    [Fact]
    public async Task Resume_elevated_emptyPassword_countsAsAttempt()
    {
        using var daemon = new FakeHostDaemon("correct-horse");
        var (runner, session, body) = BuildHarnessWithGateway("sess_sudo_3", daemon, HostExecTools());
        SeedElevatedHostExecCall(session);
        RegisterElevatedPause(session, "perm_sudo");

        using var payload = PermissionPayload("perm_sudo", "allow", password: "");
        await runner.ResumeWithPermissionAsync(payload.RootElement, CancellationToken.None);

        session.PendingIds.Should().ContainSingle().Which.Should().Be("perm_sudo");
        session.LookupSudoAttemptState("perm_sudo")!.Value.SudoAttempt.Should().Be(1,
            "an empty submit still consumes an attempt");
        daemon.VerifyPasswords.Should().Equal(new[] { "" });
        daemon.ExecCount.Should().Be(0);
        ParseSseEvents(body).Single(e => e.name == "permission_attempt")
            .data.GetProperty("attempt").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Resume_elevated_thirdWrongPassword_resolves_with_failure_outcome()
    {
        using var daemon = new FakeHostDaemon("correct-horse");
        var (runner, session, body) = BuildHarnessWithGateway("sess_sudo_4", daemon, HostExecTools());
        SeedElevatedHostExecCall(session);
        RegisterElevatedPause(session, "perm_sudo");

        using (var p1 = PermissionPayload("perm_sudo", "allow", "wrong-one"))
            await runner.ResumeWithPermissionAsync(p1.RootElement, CancellationToken.None);
        session.PendingIds.Should().ContainSingle().Which.Should().Be("perm_sudo");

        using (var p2 = PermissionPayload("perm_sudo", "allow", "wrong-two"))
            await runner.ResumeWithPermissionAsync(p2.RootElement, CancellationToken.None);
        session.PendingIds.Should().ContainSingle().Which.Should().Be("perm_sudo");

        using (var p3 = PermissionPayload("perm_sudo", "allow", "wrong-three"))
            await runner.ResumeWithPermissionAsync(p3.RootElement, CancellationToken.None);

        session.PendingIds.Should().BeEmpty("the 3rd failed attempt is terminal");
        var toolMsg = session.Messages.Last(m => m.Role == MessageRole.Tool);
        toolMsg.Content.Should().Contain("3 attempts", "the model sees the failure so it can handle it");
        toolMsg.Content.Should().Contain("NOT run");
        daemon.ExecCount.Should().Be(0, "the command never runs without a verified password");
        daemon.VerifyPasswords.Should().HaveCount(3);
        // The terminal failure must also close the client modal: a final
        // permission_attempt with terminal:true rides the last failure.
        var attempts = ParseSseEvents(body).Where(e => e.name == "permission_attempt").ToList();
        attempts.Should().HaveCount(3);
        var lastAttempt = attempts.Last().data;
        lastAttempt.GetProperty("ok").GetBoolean().Should().BeFalse();
        lastAttempt.GetProperty("terminal").GetBoolean().Should().BeTrue(
            "the last failure carries terminal:true so the webview closes the modal");
        lastAttempt.GetProperty("attempt").GetInt32().Should().Be(3);
        ParseSseEvents(body).Last().name.Should().Be("done");
    }

    [Fact]
    public async Task Resume_elevated_denial_resolves_immediately_without_password()
    {
        using var daemon = new FakeHostDaemon("correct-horse");
        var (runner, session, body) = BuildHarnessWithGateway("sess_sudo_5", daemon, HostExecTools());
        SeedElevatedHostExecCall(session);
        RegisterElevatedPause(session, "perm_sudo");

        using var payload = PermissionPayload("perm_sudo", "deny");
        await runner.ResumeWithPermissionAsync(payload.RootElement, CancellationToken.None);

        session.PendingIds.Should().BeEmpty("a denial is one-shot, never retried with a password");
        session.Messages.Last(m => m.Role == MessageRole.Tool).Content.Should().Contain("DENIED");
        daemon.VerifyCount.Should().Be(0, "a denial never verifies a password");
        daemon.ExecCount.Should().Be(0);
        ParseSseEvents(body).Last().name.Should().Be("done");
    }

    private static (AcpTurnRunner runner, AcpSession session, MemoryStream body) BuildHarnessWithGateway(
        string sessionId, FakeHostDaemon daemon, ToolRegistry? tools = null)
    {
        var session = NewSession();
        session.State.Id = sessionId;
        var gw = new HostGateway(daemon.BaseUrl, sessionId, "/tmp", daemon.Secret);
        HostGatewayRegistry.Register(sessionId, gw);
        gw.PairAsync().GetAwaiter().GetResult().Should().BeNull("pairing with the fake daemon must succeed");
        gw.Sudo.Should().Be(SudoPolicy.Ask, "the fake daemon advertises allow_sudo=ask");
        var (runner, s, body) = BuildHarness(session, tools ?? new ToolRegistry(),
            new List<List<StreamChunk>>
            {
                new() { new() { TextDelta = "ok.", IsComplete = false }, new() { IsComplete = true } },
            });
        return (runner, s, body);
    }

    private static (AcpTurnRunner runner, AcpSession session, MemoryStream body) BuildHarness(
        ToolRegistry tools,
        List<List<StreamChunk>> llmRounds,
        bool visionEnabled = false)
    {
        return BuildHarness(NewSession(), tools, llmRounds, visionEnabled);
    }

    private static (AcpTurnRunner runner, AcpSession session, MemoryStream body) BuildHarness(
        AcpSession session,
        ToolRegistry tools,
        List<List<StreamChunk>> llmRounds,
        bool visionEnabled = false)
    {
        var body = new MemoryStream();
        var writer = new SseWriter(body, CancellationToken.None);
        var config = new AppConfig { DataDirectory = Path.Combine(Path.GetTempPath(), "openmono-runner-" + Guid.NewGuid().ToString("N")[..8]) };
        config.VisionEnabled = visionEnabled;
        Directory.CreateDirectory(config.DataDirectory);
        var renderer = new TerminalRenderer();
        var llm = new ScriptedLlm(llmRounds);
        var factory = new ConversationLoopFactory(tools, config, renderer, renderer, renderer);
        var settings = new AcpServerSettings { PendingUserResponseTimeoutMinutes = 1 };
        var registry = new OpenMono.Llm.BackendRegistry();
        registry.Add(new OpenMono.Llm.BackendDefinition
        {
            Name = OpenMono.Llm.BackendRegistry.DefaultBackendName,
            Endpoint = config.Llm.Endpoint,
            DefaultModel = config.Llm.Model,
            IsLocal = true,
        });
        var pool = new OpenMono.Llm.LlmClientPool();
        pool.Set(OpenMono.Llm.BackendRegistry.DefaultBackendName, llm);
        var runner = new AcpTurnRunner(session, writer, factory, settings, registry, pool);
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
        var cached = session.Store.TryResolve(ctx.ContextKey);
        cached.Should().NotBeNull();
        cached.Allow.Should().BeTrue();
        cached.Scope.Should().Be(OpenMono.Permissions.GrantScope.Session);
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
        var cached = session.Store.TryResolve(ctx.ContextKey);
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
                // the tool-call round must be last of the pre-pause rounds to line up with the
                // "delete it" turn below.
                TextRound("chat reply 1"),
                TextRound("chat reply 2"),
                TextRound("meanwhile 0"), TextRound("meanwhile 1"), TextRound("meanwhile 2"), TextRound("meanwhile 3"), TextRound("meanwhile 4"),
                new()
                {
                    new() { ToolCallDelta = new ToolCall { Id = "call_p", Name = "AskingTool", Arguments = "{}" }, IsComplete = false },
                    new() { IsComplete = true },
                },
                TextRound("ok 1"), TextRound("ok 2"), TextRound("ok 3"), TextRound("ok 4"), TextRound("ok 5"),
            });

        // Several turns of chat first, then the risky call that pauses for a permission decision.
        // The chat turns come BEFORE the pause on purpose: a fresh user message abandons any
        // outstanding consent modal (see Missed_permission_prompt_does_not_brick_* tests), so an
        // unresolved permission must never be interleaved with later user messages.
        await runner.RunUserMessageAsync("chat 1", CancellationToken.None);
        await runner.RunUserMessageAsync("chat 2", CancellationToken.None);
        for (var i = 0; i < 5; i++)
            await runner.RunUserMessageAsync($"meanwhile {i}", CancellationToken.None);
        await runner.RunUserMessageAsync("delete it", CancellationToken.None);
        var pauseId = session.PendingIds.Single();

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

    [Fact]
    public async Task Missed_permission_prompt_does_not_brick_later_same_tool_prompt()
    {
        var tools = new ToolRegistry();
        tools.Register(new AskingTool());

        var (runner, session, body) = BuildHarness(
            tools: tools,
            llmRounds: new List<List<StreamChunk>>
            {
                // Turn 1: prompts and pauses; the user never answers — they close.
                new()
                {
                    new() { ToolCallDelta = new ToolCall { Id = "call_first", Name = "AskingTool", Arguments = "{}" }, IsComplete = false },
                    new() { IsComplete = true },
                },
                // Turn 2 after "close & resume": same tool must get a fresh card.
                new()
                {
                    new() { ToolCallDelta = new ToolCall { Id = "call_second", Name = "AskingTool", Arguments = "{}" }, IsComplete = false },
                    new() { IsComplete = true },
                },
            });

        await runner.RunUserMessageAsync("first", CancellationToken.None);
        var staleId = session.PendingIds.Single();

        // Simulate close & resume on a live process: the client reconnects against the
        // SAME cached AcpSession (live cache hands it back untouched) and sends a new
        // message instead of ever answering the outstanding card.
        body.SetLength(0);
        await runner.RunUserMessageAsync("second", CancellationToken.None);

        var events = ParseSseEvents(body);
        events.Should().Contain(e => e.name == "permission_request",
            "a new user message abandons the missed prompt; the re-prompted tool must get a " +
            "fresh card instead of the old pause silently swallowing the turn");
        events.Last(e => e.name == "permission_request").data.GetProperty("id")
            .GetString().Should().NotBe(staleId, "the reused stale pause id would resolve nothing client-side");
        events.Should().NotContain(e => e.name == "done", "the new pause must close the stream without `done`");
    }

    [Fact]
    public async Task Missed_permission_prompt_does_not_brick_later_different_tool_prompt()
    {
        var tools = new ToolRegistry();
        tools.Register(new AskingTool());
        tools.Register(new OtherAskingTool());

        var (runner, session, body) = BuildHarness(
            tools: tools,
            llmRounds: new List<List<StreamChunk>>
            {
                new()
                {
                    new() { ToolCallDelta = new ToolCall { Id = "call_first", Name = "AskingTool", Arguments = "{}" }, IsComplete = false },
                    new() { IsComplete = true },
                },
                new()
                {
                    new() { ToolCallDelta = new ToolCall { Id = "call_second", Name = "OtherAskingTool", Arguments = "{}" }, IsComplete = false },
                    new() { IsComplete = true },
                },
            });

        await runner.RunUserMessageAsync("first", CancellationToken.None);

        // Same close & resume, but the retried tool is different — this exercises the
        // queue-stuck path: _currentPermissionId still points at the abandoned pause,
        // so the new request is silently queued behind a card that can never resolve.
        body.SetLength(0);
        await runner.RunUserMessageAsync("second", CancellationToken.None);

        var events = ParseSseEvents(body);
        events.Should().Contain(e => e.name == "permission_request",
            "the abandoned pause must not leave _currentPermissionId set forever, which " +
            "silently queues every later permission behind a dead card");
        events.Last(e => e.name == "permission_request").data.GetProperty("tool")
            .GetString().Should().Be("OtherAskingTool");
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
        public int ExecuteCount { get; private set; }
        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct)
        {
            ExecuteCount++;
            return Task.FromResult(ToolResult.Success("done"));
        }
    }





    private sealed class OtherAskingTool : ITool
    {
        public string Name => "OtherAskingTool";
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
