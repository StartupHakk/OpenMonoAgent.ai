using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Llm;
using OpenMono.Permissions;
using OpenMono.Playbooks;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Playbooks;

public class PlaybookExecutorTests : IDisposable
{
    private readonly string _tempDir;

    public PlaybookExecutorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-pb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task ExecuteAsync_SavesStateUnderSessionId_SoResumeCanFindIt()
    {
        const string sessionId = "sess1234abcd";
        var playbook = new PlaybookDefinition
        {
            Name = "demo",
            Description = "demo playbook",
            Steps = [new StepDefinition { Id = "step1", InlinePrompt = "do the thing" }],
        };

        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        var renderer = new TerminalRenderer();
        var permissions = new PermissionEngine(config, renderer, renderer);
        using var executor = new PlaybookExecutor(
            new ImmediateLlmClient(), new ToolRegistry(), renderer, config, permissions);

        await executor.ExecuteAsync(
            playbook, new Dictionary<string, object>(), resumeFrom: null, sessionId, CancellationToken.None);

        // The state must be loadable with the SAME key the resume path uses (the chat session id).
        // Otherwise PlaybookState.LoadAsync(..., context.Session.Id) never matches and resume
        // silently restarts the whole playbook from step 1.
        var loaded = await PlaybookState.LoadAsync(
            config.DataDirectory, playbook.Name, sessionId, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.IsStepCompleted("step1").Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_StepOutputName_ResolvesInLaterStepTemplate()
    {
        const string sessionId = "sess-output-key";
        var playbook = new PlaybookDefinition
        {
            Name = "statetest",
            Description = "state test",
            Steps =
            [
                new StepDefinition { Id = "step_one", InlinePrompt = "say hello", Output = "greeting" },
                new StepDefinition { Id = "step_two", Requires = ["step_one"], InlinePrompt = "Value: {{state.greeting}}" },
            ],
        };

        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        var renderer = new TerminalRenderer();
        var permissions = new PermissionEngine(config, renderer, renderer);
        var llm = new EchoLlmClient();
        using var executor = new PlaybookExecutor(llm, new ToolRegistry(), renderer, config, permissions);

        await executor.ExecuteAsync(
            playbook, new Dictionary<string, object>(), resumeFrom: null, sessionId, CancellationToken.None);

        // step_two's resolved prompt (echoed back verbatim by the fake LLM) must carry
        // step_one's actual output rather than the literal unresolved "{{state.greeting}}".
        var stepTwoUserContent = llm.Calls[1].Last(m => m.Role == MessageRole.User).Content;
        stepTwoUserContent.Should().Be("Value: say hello");
    }

    [Fact]
    public async Task ExecuteAsync_GateWithoutSkipPermissions_AbortsInNonInteractiveSession()
    {
        var playbook = new PlaybookDefinition
        {
            Name = "gated",
            Description = "gated playbook",
            Steps = [new StepDefinition { Id = "step1", InlinePrompt = "do the thing", Gate = GateType.Confirm }],
        };

        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        var renderer = new TerminalRenderer();
        var permissions = new PermissionEngine(config, renderer, renderer);
        using var executor = new PlaybookExecutor(
            new ImmediateLlmClient(), new ToolRegistry(), renderer, config, permissions);

        var result = await executor.ExecuteAsync(
            playbook, new Dictionary<string, object>(), resumeFrom: null, "sess-gate-block", CancellationToken.None);

        result.Should().Contain("requires interactive confirmation");
    }

    [Fact]
    public async Task ExecuteAsync_SkipPermissions_BypassesStepGate_EvenNonInteractive()
    {
        var playbook = new PlaybookDefinition
        {
            Name = "gated",
            Description = "gated playbook",
            SkipPermissions = true,
            Steps = [new StepDefinition { Id = "step1", InlinePrompt = "do the thing", Gate = GateType.Confirm }],
        };

        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        var renderer = new TerminalRenderer();
        var permissions = new PermissionEngine(config, renderer, renderer);
        using var executor = new PlaybookExecutor(
            new ImmediateLlmClient(), new ToolRegistry(), renderer, config, permissions);

        var result = await executor.ExecuteAsync(
            playbook, new Dictionary<string, object>(), resumeFrom: null, "sess-gate-skip", CancellationToken.None);

        result.Should().NotContain("requires interactive confirmation");

        var loaded = await PlaybookState.LoadAsync(config.DataDirectory, playbook.Name, "sess-gate-skip", CancellationToken.None);
        loaded!.IsStepCompleted("step1").Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_ReportCtx_WritesCtxUsageJsonLine()
    {
        const string sessionId = "sess-ctx-write";
        var playbook = new PlaybookDefinition
        {
            Name = "ctxpb",
            Description = "ctx playbook",
            ReportCtx = true,
            Steps = [new StepDefinition { Id = "step1", InlinePrompt = "do the thing" }],
        };

        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        var renderer = new TerminalRenderer();
        var permissions = new PermissionEngine(config, renderer, renderer);
        var llm = new UsageLlmClient();
        using var executor = new PlaybookExecutor(llm, new ToolRegistry(), renderer, config, permissions);

        await executor.ExecuteAsync(
            playbook, new Dictionary<string, object>(), resumeFrom: null, sessionId, CancellationToken.None);

        var filePath = ContextUsageRecorder.CtxFilePath(config.DataDirectory);
        File.Exists(filePath).Should().BeTrue();

        var lines = File.ReadAllLines(filePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        lines.Should().HaveCount(1);

        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;
        root.GetProperty("run").GetInt32().Should().Be(1);
        root.GetProperty("playbook").GetString().Should().Be("ctxpb");
        root.GetProperty("session_id").GetString().Should().Be(sessionId);
        root.GetProperty("aborted").GetBoolean().Should().BeFalse();

        var steps = root.GetProperty("steps");
        steps.GetArrayLength().Should().Be(1);
        var step = steps[0];
        step.GetProperty("step").GetString().Should().Be("step1");
        step.GetProperty("prompt_tokens").GetInt32().Should().Be(60);
        step.GetProperty("completion_tokens").GetInt32().Should().Be(10);
        step.GetProperty("cached_tokens").GetInt32().Should().Be(25);
        step.GetProperty("peak_prompt").GetInt32().Should().Be(60);
        step.GetProperty("total_tokens").GetInt32().Should().Be(70);
        step.GetProperty("fresh_tokens").GetInt32().Should().Be(35);
        step.GetProperty("call_count").GetInt32().Should().Be(1);

        // Calls are nested under the step, not a root-level array.
        var stepCalls = step.GetProperty("calls");
        stepCalls.GetArrayLength().Should().Be(1);
        var call = stepCalls[0];
        call.GetProperty("source").GetString().Should().Be("step");
        call.GetProperty("total_tokens").GetInt32().Should().Be(70);
        call.GetProperty("fresh_tokens").GetInt32().Should().Be(35);

        var totals = root.GetProperty("totals");
        totals.GetProperty("prompt_tokens").GetInt32().Should().Be(60);
        totals.GetProperty("cached_tokens").GetInt32().Should().Be(25);
        totals.GetProperty("total_tokens").GetInt32().Should().Be(70);
        totals.GetProperty("fresh_tokens").GetInt32().Should().Be(35);
        totals.GetProperty("call_count").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_ReportCtx_CapturesToolCommand()
    {
        const string sessionId = "sess-ctx-tool";
        var playbook = new PlaybookDefinition
        {
            Name = "ctxbptool",
            Description = "ctx tool playbook",
            ReportCtx = true,
            Steps = [new StepDefinition { Id = "step1", InlinePrompt = "do the thing" }],
        };

        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        var renderer = new TerminalRenderer();
        var permissions = new PermissionEngine(config, renderer, renderer);
        using var executor = new PlaybookExecutor(
            new ToolCallUsageLlmClient(), new ToolRegistry(), renderer, config, permissions);

        var result = await executor.ExecuteAsync(
            playbook, new Dictionary<string, object>(), resumeFrom: null, sessionId, CancellationToken.None);

        var filePath = ContextUsageRecorder.CtxFilePath(config.DataDirectory);
        var lines = File.ReadAllLines(filePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        using var doc = JsonDocument.Parse(lines[0]);
        var step = doc.RootElement.GetProperty("steps")[0];
        var call = step.GetProperty("calls")[0];

        call.GetProperty("tools").GetArrayLength().Should().Be(1);
        var tool = call.GetProperty("tools")[0];
        tool.GetProperty("name").GetString().Should().Be("Bash");
        tool.GetProperty("command").GetString().Should().Be("echo hello world");
    }

    [Fact]
    public async Task ExecuteAsync_ReportCtx_AbortMidRun_StillFlushes()
    {
        const string sessionId = "sess-ctx-abort";
        // A Confirm gate without skip-permissions aborts in a non-interactive session before running
        // the step's LLM call. Even so, the ctx-usage line must be written so aborted runs are visible.
        var playbook = new PlaybookDefinition
        {
            Name = "ctxabort",
            Description = "ctx abort playbook",
            ReportCtx = true,
            Steps = [new StepDefinition { Id = "step1", InlinePrompt = "do the thing", Gate = GateType.Confirm }],
        };

        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        var renderer = new TerminalRenderer();
        var permissions = new PermissionEngine(config, renderer, renderer);
        using var executor = new PlaybookExecutor(
            new UsageLlmClient(), new ToolRegistry(), renderer, config, permissions);

        var result = await executor.ExecuteAsync(
            playbook, new Dictionary<string, object>(), resumeFrom: null, sessionId, CancellationToken.None);

        result.Should().Contain("requires interactive confirmation");

        var filePath = ContextUsageRecorder.CtxFilePath(config.DataDirectory);
        File.Exists(filePath).Should().BeTrue();
        var lines = File.ReadAllLines(filePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        lines.Should().HaveCount(1);

        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;
        root.GetProperty("run").GetInt32().Should().Be(1);
        root.GetProperty("playbook").GetString().Should().Be("ctxabort");
        root.GetProperty("aborted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_ReportCtx_RerunAppendsIncrementingRun()
    {
        const string sessionId = "sess-ctx-rerun";
        var playbook = new PlaybookDefinition
        {
            Name = "ctxrerun",
            Description = "ctx rerun playbook",
            ReportCtx = true,
            Steps = [new StepDefinition { Id = "step1", InlinePrompt = "do the thing" }],
        };

        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        var renderer = new TerminalRenderer();
        var permissions = new PermissionEngine(config, renderer, renderer);
        var llm = new UsageLlmClient();

        using (var executor = new PlaybookExecutor(llm, new ToolRegistry(), renderer, config, permissions))
        {
            await executor.ExecuteAsync(
                playbook, new Dictionary<string, object>(), resumeFrom: null, sessionId, CancellationToken.None);
        }

        using (var executor = new PlaybookExecutor(llm, new ToolRegistry(), renderer, config, permissions))
        {
            await executor.ExecuteAsync(
                playbook, new Dictionary<string, object>(), resumeFrom: null, sessionId, CancellationToken.None);
        }

        var filePath = ContextUsageRecorder.CtxFilePath(config.DataDirectory);
        var lines = File.ReadAllLines(filePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        lines.Should().HaveCount(2);

        using (var doc = JsonDocument.Parse(lines[0]))
        {
            doc.RootElement.GetProperty("run").GetInt32().Should().Be(1);
        }
        using (var doc = JsonDocument.Parse(lines[1]))
        {
            doc.RootElement.GetProperty("run").GetInt32().Should().Be(2);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithoutReportCtx_WritesNoCtxFile()
    {
        const string sessionId = "sess-ctx-off";
        var playbook = new PlaybookDefinition
        {
            Name = "noctx",
            Description = "no ctx playbook",
            Steps = [new StepDefinition { Id = "step1", InlinePrompt = "do the thing" }],
        };

        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        var renderer = new TerminalRenderer();
        var permissions = new PermissionEngine(config, renderer, renderer);
        using var executor = new PlaybookExecutor(
            new UsageLlmClient(), new ToolRegistry(), renderer, config, permissions);

        await executor.ExecuteAsync(
            playbook, new Dictionary<string, object>(), resumeFrom: null, sessionId, CancellationToken.None);

        var filePath = ContextUsageRecorder.CtxFilePath(config.DataDirectory);
        File.Exists(filePath).Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private sealed class UsageLlmClient : ILlmClient
    {
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages,
            JsonElement? tools,
            LlmOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new StreamChunk { TextDelta = "hi", IsComplete = true, Usage = new UsageInfo
            {
                PromptTokens = 60,
                CompletionTokens = 10,
                CachedTokens = 25,
            } };
            await Task.CompletedTask;
        }

        public void Dispose() { }
    }

    // Yields a Bash tool call + usage on the first request, then plain text so the executor's
    // tool loop can terminate. Lets the context recorder capture the issued command.
    private sealed class ToolCallUsageLlmClient : ILlmClient
    {
        private int _invocations;

        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages,
            JsonElement? tools,
            LlmOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            _invocations++;
            if (_invocations == 1)
            {
                yield return new StreamChunk
                {
                    ToolCallDelta = new ToolCall
                    {
                        Id = "t1",
                        Name = "Bash",
                        Arguments = "{\"command\":\"echo hello world\"}",
                    },
                    Usage = new UsageInfo { PromptTokens = 50, CompletionTokens = 5, CachedTokens = 20 },
                    IsComplete = true,
                };
            }
            else
            {
                yield return new StreamChunk { TextDelta = "done", IsComplete = true };
            }
            await Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private sealed class EchoLlmClient : ILlmClient
    {
        public readonly List<IReadOnlyList<Message>> Calls = [];

        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages,
            JsonElement? tools,
            LlmOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Calls.Add(messages);
            var lastUser = messages.Last(m => m.Role == MessageRole.User).Content;
            yield return new StreamChunk { TextDelta = lastUser, IsComplete = true };
            await Task.CompletedTask;
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task RetryOnAbort_RetriesDoomAbortInternally_ThenHardAborts()
    {
        // The fake model repeats the identical tool call forever; the dispatcher's doom guard
        // escalates on the 7th identical batch (2 execute + 5 blocked: N,N,SN,SN,Escalate).
        // With retry-on-abort + limit 2 the executor must run 3 attempts back-to-back with no
        // model recovery turn in between, record 2 abort entries, then hard-abort.
        const string sessionId = "sess-retry-loop";
        var playbook = new PlaybookDefinition
        {
            Name = "loopy",
            Description = "doom-loops every attempt",
            AllowedTools = ["LoopTool"],
            RetryOnAbort = true,
            RetryAttemptLimit = 2,
            Steps = [new StepDefinition { Id = "loop", InlinePrompt = "repeat the call" }],
        };

        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        var renderer = new TerminalRenderer();
        var permissions = new PermissionEngine(config, renderer, renderer);
        var registry = new ToolRegistry();
        registry.Register(new LoopTool());
        var llm = new LoopLlmClient();
        using var executor = new PlaybookExecutor(llm, registry, renderer, config, permissions);

        var result = await executor.ExecuteDetailedAsync(
            playbook, new Dictionary<string, object>(), resumeFrom: null, sessionId, CancellationToken.None);

        result.Abort.Should().NotBeNull();
        result.Abort!.ErrorCode.Should().Be(PlaybookExecutor.PlaybookAbortCodes.DoomLoopEscalated);
        result.Abort.StepId.Should().Be("loop");
        result.Output.Should().Contain("retry-on-abort: 2/2");
        // 7 identical batches per attempt (escalation on the 7th), 3 attempts, and NO extra
        // model call between abort and re-run — the retry is internal, not a recovery turn.
        llm.Invocations.Should().Be(21);

        var loaded = await PlaybookState.LoadAsync(
            config.DataDirectory, playbook.Name, sessionId, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.Aborts.Should().HaveCount(2, "each retried attempt is recorded; the final hard abort travels in the result, not the state file");
        loaded.Aborts[0].Attempt.Should().Be(1);
        loaded.Aborts[1].Attempt.Should().Be(2);
        loaded.Aborts.Should().OnlyContain(a => a.MaxAttempts == 2);
        loaded.Aborts.Should().OnlyContain(a => a.Step == "loop");
        loaded.Aborts.Should().OnlyContain(a => a.Code == PlaybookExecutor.PlaybookAbortCodes.DoomLoopEscalated, "the abort code threads through so harnesses can label rows correctly");
        loaded.Aborts.Should().OnlyContain(a => !string.IsNullOrWhiteSpace(a.Pattern), "the repeating-pattern signature must travel with the abort");
        loaded.Aborts.Should().OnlyContain(a => !string.IsNullOrWhiteSpace(a.At));
    }

    [Fact]
    public async Task RetryOnAbort_Disabled_HardAbortsImmediately()
    {
        const string sessionId = "sess-no-retry";
        var playbook = new PlaybookDefinition
        {
            Name = "loopy-once",
            Description = "doom-loops, no retry configured",
            AllowedTools = ["LoopTool"],
            Steps = [new StepDefinition { Id = "loop", InlinePrompt = "repeat the call" }],
        };

        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        var renderer = new TerminalRenderer();
        var permissions = new PermissionEngine(config, renderer, renderer);
        var registry = new ToolRegistry();
        registry.Register(new LoopTool());
        var llm = new LoopLlmClient();
        using var executor = new PlaybookExecutor(llm, registry, renderer, config, permissions);

        var result = await executor.ExecuteDetailedAsync(
            playbook, new Dictionary<string, object>(), resumeFrom: null, sessionId, CancellationToken.None);

        result.Abort.Should().NotBeNull();
        result.Abort!.ErrorCode.Should().Be(PlaybookExecutor.PlaybookAbortCodes.DoomLoopEscalated);
        result.Output.Should().NotContain("retry-on-abort");
        llm.Invocations.Should().Be(7, "a single attempt escalates on the 7th identical batch with no re-run");
    }

    [Fact]
    public async Task NonDoomAbort_NeverRetries()
    {
        const string sessionId = "sess-no-doom";
        var playbook = new PlaybookDefinition
        {
            Name = "missing-dep",
            Description = "fails on a missing dependency",
            AllowedTools = ["LoopTool"],
            RetryOnAbort = true,
            RetryAttemptLimit = 2,
            Steps = [new StepDefinition { Id = "step1", InlinePrompt = "do it", Requires = ["nope"] }],
        };

        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        var renderer = new TerminalRenderer();
        var permissions = new PermissionEngine(config, renderer, renderer);
        var registry = new ToolRegistry();
        registry.Register(new LoopTool());
        var llm = new LoopLlmClient();
        using var executor = new PlaybookExecutor(llm, registry, renderer, config, permissions);

        var result = await executor.ExecuteDetailedAsync(
            playbook, new Dictionary<string, object>(), resumeFrom: null, sessionId, CancellationToken.None);

        result.Abort.Should().NotBeNull();
        result.Abort!.ErrorCode.Should().Be(PlaybookExecutor.PlaybookAbortCodes.DependencyMissing);
        llm.Invocations.Should().Be(0, "a non-doom abort never triggers the retry loop");
    }

    // A model that makes progress (a unique call every round) but never finishes: the step
    // burns max-tool-loops each attempt, retries within budget like a doom abort, then
    // hard-aborts with the distinct tool_loop_exhausted code.
    [Fact]
    public async Task ToolLoopExhausted_RetriesWithinBudget_ThenHardAborts()
    {
        const string sessionId = "sess-tool-loop";
        var playbook = new PlaybookDefinition
        {
            Name = "busywork",
            Description = "never finishes a step",
            AllowedTools = ["LoopTool"],
            MaxToolLoops = 3,
            RetryOnAbort = true,
            RetryAttemptLimit = 2,
            Steps = [new StepDefinition { Id = "step1", InlinePrompt = "keep working" }],
        };

        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        var renderer = new TerminalRenderer();
        var permissions = new PermissionEngine(config, renderer, renderer);
        var registry = new ToolRegistry();
        registry.Register(new LoopTool());
        var llm = new VaryingLlmClient();
        using var executor = new PlaybookExecutor(llm, registry, renderer, config, permissions);

        var result = await executor.ExecuteDetailedAsync(
            playbook, new Dictionary<string, object>(), resumeFrom: null, sessionId, CancellationToken.None);

        result.Abort.Should().NotBeNull();
        result.Abort!.ErrorCode.Should().Be(PlaybookExecutor.PlaybookAbortCodes.ToolLoopExhausted);
        result.Abort.StepId.Should().Be("step1");
        result.Output.Should().Contain(PlaybookExecutor.ToolLoopExhaustedMarker);
        result.Output.Should().Contain("retry-on-abort: 2/2");
        llm.Invocations.Should().Be(9, "3 tool-loop rounds per attempt × 3 attempts (initial + 2 re-runs), then escalate");

        var loaded = await PlaybookState.LoadAsync(
            config.DataDirectory, playbook.Name, sessionId, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.Aborts.Should().HaveCount(2, "each retried attempt is recorded");
        loaded.Aborts.Should().OnlyContain(a => a.Code == PlaybookExecutor.PlaybookAbortCodes.ToolLoopExhausted);
    }

    [Fact]
    public async Task ToolLoopExhausted_Disabled_HardAbortsImmediately()
    {
        const string sessionId = "sess-tool-loop-once";
        var playbook = new PlaybookDefinition
        {
            Name = "busywork-once",
            Description = "never finishes a step, no retry configured",
            AllowedTools = ["LoopTool"],
            MaxToolLoops = 3,
            Steps = [new StepDefinition { Id = "step1", InlinePrompt = "keep working" }],
        };

        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        var renderer = new TerminalRenderer();
        var permissions = new PermissionEngine(config, renderer, renderer);
        var registry = new ToolRegistry();
        registry.Register(new LoopTool());
        var llm = new VaryingLlmClient();
        using var executor = new PlaybookExecutor(llm, registry, renderer, config, permissions);

        var result = await executor.ExecuteDetailedAsync(
            playbook, new Dictionary<string, object>(), resumeFrom: null, sessionId, CancellationToken.None);

        result.Abort.Should().NotBeNull();
        result.Abort!.ErrorCode.Should().Be(PlaybookExecutor.PlaybookAbortCodes.ToolLoopExhausted);
        result.Output.Should().NotContain("retry-on-abort");
        llm.Invocations.Should().Be(3, "a single attempt burns the budget with no re-run");
    }

    // Emits a unique tool call every round so the doom-loop guard never fires — isolates the
    // tool-loop budget from the doom-loop budget.
    private sealed class VaryingLlmClient : ILlmClient
    {
        public int Invocations { get; private set; }

        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages,
            JsonElement? tools,
            LlmOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Invocations++;
            yield return new StreamChunk
            {
                ToolCallDelta = new ToolCall { Id = $"t{Invocations}", Name = "LoopTool", Arguments = $"{{\"n\":{Invocations}}}" },
                IsComplete = true,
            };
            await Task.CompletedTask;
        }

        public void Dispose() { }
    }

    // Always emits the identical tool call so the dispatcher's doom-loop guard escalates.
    private sealed class LoopLlmClient : ILlmClient
    {
        public int Invocations { get; private set; }

        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages,
            JsonElement? tools,
            LlmOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Invocations++;
            yield return new StreamChunk
            {
                ToolCallDelta = new ToolCall { Id = "t1", Name = "LoopTool", Arguments = "{}" },
                IsComplete = true,
            };
            await Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private sealed class LoopTool : ToolBase
    {
        public override string Name => "LoopTool";
        public override string Description => "test";
        public override bool IsReadOnly => true;
        public override bool IsConcurrencySafe => true;
        public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

        protected override SchemaBuilder DefineSchema() => new();

        protected override Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct) =>
            Task.FromResult(ToolResult.Success("ran"));
    }

    private sealed class ImmediateLlmClient : ILlmClient
    {
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages,
            JsonElement? tools,
            LlmOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new StreamChunk { TextDelta = "done", IsComplete = true };
            await Task.CompletedTask;
        }

        public void Dispose() { }
    }
}
