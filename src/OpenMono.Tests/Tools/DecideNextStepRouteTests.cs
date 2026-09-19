using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Decisions;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Tools;

public class DecideNextStepRouteTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DecideNextStepTool _tool;
    private readonly ToolRegistry _registry;

    public DecideNextStepRouteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var options = new DecisionOptions(true, 0.85, 0.6, 0.5, 64);
        _tool = new DecideNextStepTool(options);
        _registry = new ToolRegistry();
        _registry.Register(new DecideRankTool(options));
        _registry.Register(new DecideVerifyTool(options));
    }

    private ToolContext Context() => new()
    {
        ToolRegistry = _registry,
        Session = new SessionState(),
        Permissions = new PermissionEngine(new AppConfig(), new TerminalRenderer(), new TerminalRenderer()),
        Config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir },
        WorkingDirectory = _tempDir,
        WriteOutput = _ => { },
        AskUser = (_, _) => Task.FromResult(""),
    };

    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Route_HeuristicPicksRankedTool()
    {
        var input = Input("""{"goal": "rank candidates", "last_action": "search", "result": "have items", "attempts": 0, "mode": "route"}""");

        var result = await _tool.ExecuteAsync(input, Context(), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("decide_rank");
    }

    [Fact]
    public async Task Route_UnknownChoiceFallsBackToAskUser()
    {
        var input = Input("""{"goal": "rank", "last_action": "x", "result": "y", "attempts": 0, "mode": "route", "choice": "missing_tool"}""");

        var result = await _tool.ExecuteAsync(input, Context(), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("ask_user");
    }

    [Fact]
    public async Task Route_KnownChoiceIsHonored()
    {
        var input = Input("""{"goal": "rank", "last_action": "x", "result": "y", "attempts": 0, "mode": "route", "choice": "decide_verify"}""");

        var result = await _tool.ExecuteAsync(input, Context(), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("decide_verify");
    }

    [Fact]
    public async Task Route_EmptyMenuIsInvalidInput()
    {
        var input = Input("""{"goal": "rank", "last_action": "x", "result": "y", "attempts": 0, "mode": "route", "exclude_tools": "*"}""");

        var result = await _tool.ExecuteAsync(input, Context(), CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task VerdictPathIgnoresRouteFields()
    {
        var input = Input("""{"goal": "finish task", "last_action": "write", "result": "done and completed successfully", "attempts": 0}""");

        var result = await _tool.ExecuteAsync(input, Context(), CancellationToken.None);

        result.IsError.Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
        GC.SuppressFinalize(this);
    }
}
