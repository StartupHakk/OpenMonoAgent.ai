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
