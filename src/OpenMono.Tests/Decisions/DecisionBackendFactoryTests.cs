using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Decisions;
using OpenMono.Llm;
using OpenMono.Permissions;
using OpenMono.Playbooks;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Decisions;

public class DecisionBackendFactoryTests : IDisposable
{
    private readonly string _tempDir;

    public DecisionBackendFactoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    private static DecisionOptions Options(string? backend = null)
    {
        var options = new DecisionOptions(false, 0.85, 0.6, 0.5, 64);
        return backend is null ? options : options with { Backend = backend };
    }

    private sealed class StubBackend : IDecisionBackend
    {
        public string SeenState { get; private set; } = "";
        public int ChooseCalls { get; private set; }

        public (string Choice, double Confidence, IReadOnlyDictionary<string, double> Probabilities) Choose(
            string state, IReadOnlyDictionary<string, string?> options, CancellationToken ct = default)
        {
            ChooseCalls++;
            SeenState = state;
            var pick = options.Keys.Last();
            var probs = options.ToDictionary(kv => kv.Key, kv => kv.Key == pick ? 1.0 : 0.0);
            return (pick, 1.0, probs);
        }

        public double JudgeTrue(string state, string proposition, CancellationToken ct = default) => 0.99;

        public double Relevance(string query, string candidate, CancellationToken ct = default) => 0.99;

        public (string Verdict, double Confidence) Verify(
            string evidence, string claim, CancellationToken ct = default) => ("supported", 0.99);
    }

    private sealed class FixedChoiceBackend(string choice, double confidence) : IDecisionBackend
    {
        public (string Choice, double Confidence, IReadOnlyDictionary<string, double> Probabilities) Choose(
            string state, IReadOnlyDictionary<string, string?> options, CancellationToken ct = default) =>
            (choice, confidence, options.ToDictionary(kv => kv.Key, _ => 1.0 / options.Count));

        public double JudgeTrue(string state, string proposition, CancellationToken ct = default) => 0.5;

        public double Relevance(string query, string candidate, CancellationToken ct = default) => 0.5;

        public (string Verdict, double Confidence) Verify(
            string evidence, string claim, CancellationToken ct = default) => ("not_addressed", 0.5);
    }

    [Fact]
    public void Factory_DefaultsToHeuristic()
    {
        DecisionBackendFactory.Create(Options()).Should().BeOfType<HeuristicBackend>();
    }

    [Fact]
    public void Factory_ExplicitHeuristic_ReturnsHeuristic()
    {
        DecisionBackendFactory.Create(Options("heuristic")).Should().BeOfType<HeuristicBackend>();
    }

    [Fact]
    public void Factory_UnknownName_FallsBackToHeuristic()
    {
        DecisionBackendFactory.Create(Options("systemone")).Should().BeOfType<HeuristicBackend>();
        DecisionBackendFactory.Create(Options("typo-backend")).Should().BeOfType<HeuristicBackend>();
    }

    [Fact]
    public void Settings_BackendDefaultsToHeuristic()
    {
        new DecisionSettings().Backend.Should().Be("heuristic");
        DecisionOptions.FromSettings(new DecisionSettings()).Backend.Should().Be("heuristic");
    }

    [Fact]
    public void Settings_BackendFlowsThroughMergeAndOptions()
    {
        var settings = new DecisionSettings();
        settings.MergeFrom(new DecisionSettings { Backend = "  Heuristic " });
        DecisionOptions.FromSettings(settings).Backend.Should().Be("heuristic");
    }

    [Fact]
    public void Tools_ConstructWithStubBackend()
    {
        var options = Options();
        var stub = new StubBackend();

        new DecideEvaluateTool(options, stub).Should().NotBeNull();
        new DecideRankTool(options, stub).Should().NotBeNull();
        new DecideVerifyTool(options, stub).Should().NotBeNull();
        new DecideNextStepTool(options, stub).Should().NotBeNull();
        new DecisionGateTool(options, _tempDir, stub).Should().NotBeNull();
        new ChiefRouter(options, _tempDir, stub).Should().NotBeNull();
    }

    [Fact]
    public void Tools_DefaultToHeuristicBackend_WithIdenticalBehavior()
    {
        var options = Options();
        var menu = new Dictionary<string, string?> { ["a"] = "first", ["b"] = "second" };

        var fromTool = new DecideNextStepTool(options);
        var direct = new HeuristicBackend(options);

        fromTool.Should().NotBeNull();
        direct.Choose("state", menu).Should().BeEquivalentTo(new HeuristicBackend(options).Choose("state", menu));
    }

    [Fact]
    public async Task Evaluate_UsesInjectedBackendChoice()
    {
        var options = Options();
        var tool = new DecideEvaluateTool(options, new StubBackend());
        var input = JsonDocument.Parse(
            """{"state": {"notes": "evidence"}, "questions": {"q1": {"type": "choice", "instructions": "pick", "criteria": {"a": "first", "b": "second"}}}}""").RootElement;

        var result = await tool.ExecuteAsync(input, TestContext(), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("\"b\"");
    }

    [Fact]
    public async Task ChiefRouter_UsesInjectedBackend()
    {
        var options = new DecisionOptions(true, 0.85, 0.6, 0.5, 64);
        var stub = new StubBackend();
        var router = new ChiefRouter(options, _tempDir, stub);

        var (handoff, _) = await router.RouteAsync(
            "Compare tools", "Sources collected. Evidence gathered. Findings ready.", 0.0, CancellationToken.None);

        stub.ChooseCalls.Should().Be(1);
        stub.SeenState.Should().Contain("Compare tools");
        handoff.Choice.Should().Be("review");
    }

    [Fact]
    public async Task JudgeGate_InjectedBackendAutoContinue_ReturnsNull()
    {
        using var executor = TestExecutor();
        var playbook = new PlaybookDefinition { Name = "judged", Description = "judge test" };
        var step = new StepDefinition { Id = "s1", InlinePrompt = "do it", Gate = GateType.Judge };
        var state = new PlaybookState { PlaybookName = "judged", SessionId = "sess-j1" };

        var result = await executor.HandleJudgeGateAsync(
            playbook, step, state, "content", CancellationToken.None,
            new FixedChoiceBackend("auto_continue", 0.99));

        result.Should().BeNull();
        state.Judgments.Should().ContainKey("s1");
    }

    [Fact]
    public async Task JudgeGate_InjectedBackendEscalate_Aborts()
    {
        using var executor = TestExecutor();
        var playbook = new PlaybookDefinition { Name = "judged", Description = "judge test" };
        var step = new StepDefinition { Id = "s1", InlinePrompt = "do it", Gate = GateType.Judge };
        var state = new PlaybookState { PlaybookName = "judged", SessionId = "sess-j2" };

        var result = await executor.HandleJudgeGateAsync(
            playbook, step, state, "content", CancellationToken.None,
            new FixedChoiceBackend("escalate", 0.99));

        result.Should().NotBeNull();
    }

    private PlaybookExecutor TestExecutor()
    {
        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        var renderer = new TerminalRenderer();
        var permissions = new PermissionEngine(config, renderer, renderer);
        return new PlaybookExecutor(new FakeLlmClient(), new ToolRegistry(), renderer, config, permissions);
    }

    private sealed class FakeLlmClient : ILlmClient
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

    private ToolContext TestContext()
    {
        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        var renderer = new TerminalRenderer();
        return new ToolContext
        {
            ToolRegistry = new ToolRegistry(),
            Session = new SessionState(),
            Permissions = new PermissionEngine(config, renderer, renderer),
            Config = config,
            WorkingDirectory = _tempDir,
            WriteOutput = _ => { },
            AskUser = (_, _) => Task.FromResult(""),
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
        GC.SuppressFinalize(this);
    }
}
