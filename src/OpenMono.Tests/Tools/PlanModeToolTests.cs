using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Tools;

public class PlanModeToolTests
{
    [Fact]
    public async Task EnterPlanMode_Succeeds()
    {
        var tool = new EnterPlanModeTool();
        var context = CreateContext();

        var input = JsonDocument.Parse("""{"reason": "complex refactoring"}""").RootElement;
        var result = await tool.ExecuteAsync(input, context, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("plan mode");
        context.Session.Meta.PlanMode.Should().BeTrue();
    }

    [Fact]
    public async Task ExitPlanMode_Succeeds()
    {
        var context = CreateContext();
        context.Session.Meta.PlanMode = true;

        var tool = new ExitPlanModeTool();
        var input = JsonDocument.Parse("""{"plan": "Step 1: Read files\nStep 2: Edit code"}""").RootElement;
        var result = await tool.ExecuteAsync(input, context, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Step 1");
        context.Session.Meta.PlanMode.Should().BeFalse();
    }

    [Fact]
    public async Task EnterPlanMode_AlreadyInPlanMode_ReturnsError()
    {
        var context = CreateContext();
        context.Session.Meta.PlanMode = true;

        var tool = new EnterPlanModeTool();
        var input = JsonDocument.Parse("""{"reason": "test"}""").RootElement;
        var result = await tool.ExecuteAsync(input, context, CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ExitPlanMode_NotInPlanMode_ReturnsError()
    {
        var tool = new ExitPlanModeTool();
        var context = CreateContext();

        var input = JsonDocument.Parse("""{"plan": "some plan"}""").RootElement;
        var result = await tool.ExecuteAsync(input, context, CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public void EnterPlanMode_IsAutoAllow()
    {
        var tool = new EnterPlanModeTool();
        var input = JsonDocument.Parse("{}").RootElement;
        tool.RequiredPermission(input).Should().Be(PermissionLevel.AutoAllow);
    }

    [Fact]
    public void ExitPlanMode_IsAutoAllow()
    {
        var tool = new ExitPlanModeTool();
        var input = JsonDocument.Parse("{}").RootElement;
        tool.RequiredPermission(input).Should().Be(PermissionLevel.AutoAllow);
    }

    private static ToolContext CreateContext()
    {
        var workDir = Path.GetTempPath();
        return new()
        {
            ToolRegistry = new ToolRegistry(),
            Session = new SessionState(),
            Permissions = new PermissionEngine(new AppConfig(), new TerminalRenderer(), new TerminalRenderer()),
            Config = new AppConfig { WorkingDirectory = workDir },
            WorkingDirectory = workDir,
            WriteOutput = _ => { },
            AskUser = (_, _) => Task.FromResult(""),
        };
    }
}
