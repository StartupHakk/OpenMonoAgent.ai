using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Tools;

public class AskUserToolTests
{
    private readonly AskUserTool _tool = new();

    private static ToolContext CreateContext(Func<string, string> answer, out List<string> asked)
    {
        var captured = new List<string>();
        asked = captured;
        return new ToolContext
        {
            ToolRegistry = new ToolRegistry(),
            Session = new SessionState(),
            Permissions = new PermissionEngine(new AppConfig(), new TerminalRenderer(), new TerminalRenderer()),
            Config = new AppConfig { WorkingDirectory = Path.GetTempPath() },
            WorkingDirectory = Path.GetTempPath(),
            WriteOutput = _ => { },
            AskUser = (q, _) => { captured.Add(q); return Task.FromResult(answer(q)); },
        };
    }

    [Fact]
    public void IsReadOnly_And_AutoAllow_SoItWorksInPlanMode()
    {
        _tool.IsReadOnly.Should().BeTrue();
        _tool.DefaultPermission.Should().Be(PermissionLevel.AutoAllow);
        PlanModePolicy.IsToolAllowed(_tool).Should().BeTrue();
    }

    [Fact]
    public async Task Execute_ReturnsUsersAnswer()
    {
        var context = CreateContext(_ => "Next.js", out var asked);
        var input = JsonDocument.Parse("""{"question": "Which stack should I use?"}""").RootElement;

        var result = await _tool.ExecuteAsync(input, context, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Be("Next.js");
        asked.Single().Should().Contain("Which stack should I use?");
    }

    [Fact]
    public async Task Execute_WithOptions_PresentsNumberedChoices()
    {
        var context = CreateContext(_ => "2", out var asked);
        var input = JsonDocument.Parse(
            """{"question": "Pick a stack", "options": ["Node.js", "Python", "Go"]}""").RootElement;

        var result = await _tool.ExecuteAsync(input, context, CancellationToken.None);

        result.Content.Should().Be("2");
        asked.Single().Should().Contain("[1] Node.js");
        asked.Single().Should().Contain("[2] Python");
        asked.Single().Should().Contain("[3] Go");
    }
}
