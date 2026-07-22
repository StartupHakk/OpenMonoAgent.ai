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

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
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
