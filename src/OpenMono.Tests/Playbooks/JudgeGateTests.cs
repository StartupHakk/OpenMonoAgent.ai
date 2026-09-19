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

public class JudgeGateTests : IDisposable
{
    private readonly string _tempDir;

    public JudgeGateTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void LoadAll_ParsesJudgeGateWithQuestionAndThreshold()
    {
        var playbookDir = Path.Combine(_tempDir, "review-flow");
        Directory.CreateDirectory(playbookDir);
        File.WriteAllText(Path.Combine(playbookDir, "PLAYBOOK.md"), """
            ---
            name: review-flow
            description: gated flow
            allowed-tools:
              - FileRead
            steps:
              - id: check
                inline-prompt: Check the workspace.
                gate: judge
                judge:
                  question: Is it safe to proceed?
                  threshold: 0.9
            ---

            Body.
            """);

        var loader = new PlaybookLoader([_tempDir]);
        var playbooks = loader.LoadAll();

        playbooks.Should().HaveCount(1);
        playbooks[0].Steps.Should().HaveCount(1);
        playbooks[0].Steps[0].Gate.Should().Be(GateType.Judge);
        playbooks[0].Steps[0].JudgeQuestion.Should().Be("Is it safe to proceed?");
        playbooks[0].Steps[0].JudgeThreshold.Should().Be(0.9);
    }

    [Fact]
    public void LoadAll_SkipsStepWithUnknownGate()
    {
        var playbookDir = Path.Combine(_tempDir, "weird");
        Directory.CreateDirectory(playbookDir);
        File.WriteAllText(Path.Combine(playbookDir, "PLAYBOOK.md"), """
            ---
            name: weird
            description: weird gates
            allowed-tools:
              - FileRead
            steps:
              - id: odd
                inline-prompt: Do odd things.
                gate: telepathy
            ---

            Body.
            """);

        var loader = new PlaybookLoader([_tempDir]);
        var playbooks = loader.LoadAll();

        playbooks.Should().HaveCount(1);
        playbooks[0].Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_RendersLatestJudgment()
    {
        var state = new PlaybookState
        {
            PlaybookName = "p",
            SessionId = "s",
        };
        state.RecordJudgment("step1", "[auto_continue 0.91] proceed");
        var playbook = new PlaybookDefinition { Name = "p", Description = "d" };

        var resolved = await TemplateEngine.ResolveAsync(
            "Last: {{judgment}}", state, playbook, _tempDir, CancellationToken.None);

        resolved.Should().Be("Last: [auto_continue 0.91] proceed");
    }

    [Fact]
    public async Task ResolveAsync_RendersEmptyJudgmentWhenNoneRan()
    {
        var state = new PlaybookState { PlaybookName = "p", SessionId = "s" };
        var playbook = new PlaybookDefinition { Name = "p", Description = "d" };

        var resolved = await TemplateEngine.ResolveAsync(
            "Last: {{judgment}}", state, playbook, _tempDir, CancellationToken.None);

        resolved.Should().Be("Last: ");
    }

    [Fact]
    public async Task ExecuteAsync_JudgeAutoContinueRunsStepAndRecordsJudgment()
    {
        const string sessionId = "sess-judge-auto";
        var playbook = new PlaybookDefinition
        {
            Name = "judged",
            Description = "Ship the release, evidence supports proceeding",
            Steps = [new StepDefinition { Id = "deploy", InlinePrompt = "deploy now", Gate = GateType.Judge }],
        };

        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        var renderer = new TerminalRenderer();
        var permissions = new PermissionEngine(config, renderer, renderer);
        using var executor = new PlaybookExecutor(
            new ImmediateLlmClient(), new ToolRegistry(), renderer, config, permissions);

        var result = await executor.ExecuteAsync(
            playbook, new Dictionary<string, object>(), resumeFrom: null, sessionId, CancellationToken.None);

        result.Should().Be("done");
        var loaded = await PlaybookState.LoadAsync(config.DataDirectory, playbook.Name, sessionId, CancellationToken.None);
        loaded.Should().NotBeNull();
        loaded!.Judgments["deploy"].Should().Contain("auto_continue");
    }

    [Fact]
    public async Task ExecuteAsync_JudgeEscalateAbortsInNonInteractiveSession()
    {
        const string sessionId = "sess-judge-escalate";
        var playbook = new PlaybookDefinition
        {
            Name = "blockedflow",
            Description = "Migrate database",
            Steps =
            [
                new StepDefinition
                {
                    Id = "migrate",
                    InlinePrompt = "Blocked, unclear how to proceed, need user decision",
                    Gate = GateType.Judge,
                },
            ],
        };

        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        var renderer = new TerminalRenderer();
        var permissions = new PermissionEngine(config, renderer, renderer);
        using var executor = new PlaybookExecutor(
            new ImmediateLlmClient(), new ToolRegistry(), renderer, config, permissions);

        var result = await executor.ExecuteAsync(
            playbook, new Dictionary<string, object>(), resumeFrom: null, sessionId, CancellationToken.None);

        result.Should().Contain("aborted");
        var loaded = await PlaybookState.LoadAsync(config.DataDirectory, playbook.Name, sessionId, CancellationToken.None);
        (loaded is null || !loaded.IsStepCompleted("migrate")).Should().BeTrue();
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

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
        GC.SuppressFinalize(this);
    }
}
