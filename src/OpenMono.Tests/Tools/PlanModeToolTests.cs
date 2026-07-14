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
    public async Task CreatePlan_presents_plan_and_STAYS_in_plan_mode()
    {
        var context = CreateContext();
        context.Session.Meta.PlanMode = true;

        var tool = new CreatePlanTool();
        var input = JsonDocument.Parse("""{"plan": "Step 1: Read files\nStep 2: Edit code"}""").RootElement;
        var result = await tool.ExecuteAsync(input, context, CancellationToken.None);

        result.IsError.Should().BeFalse();
        // The plan content is stored for the card/TUI to render; the brief result names the
        // proceed-options and must not leak agent-facing instructions to the user.
        context.Session.Meta.LastPlanContent.Should().Contain("Step 1");
        result.Content.Should().Contain("Auto implement");
        result.Content.Should().Contain("Ask before edits");
        result.Content.Should().Contain("Keep planning");
        result.Content.Should().NotContain("do not write");
        context.Session.Meta.PlanMode.Should().BeTrue("CreatePlan only presents — it must NOT drop to Build");
    }

    [Fact]
    public async Task CreatePlan_NotInPlanMode_ReturnsError()
    {
        var tool = new CreatePlanTool();
        var context = CreateContext(); // build mode

        var input = JsonDocument.Parse("""{"plan": "some plan"}""").RootElement;
        var result = await tool.ExecuteAsync(input, context, CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task ImplementPlan_switches_to_build_mode()
    {
        var context = CreateContext();
        context.Session.Meta.PlanMode = true;
        context.Session.Meta.LastPlanContent = "Step 1: do the thing";

        var tool = new ImplementPlanTool();
        var result = await tool.ExecuteAsync(JsonDocument.Parse("{}").RootElement, context, CancellationToken.None);

        result.IsError.Should().BeFalse();
        context.Session.Meta.PlanMode.Should().BeFalse("ImplementPlan flips Plan → Build");
        result.Content.Should().Contain("Build mode");
    }

    [Fact]
    public async Task ImplementPlan_AlreadyBuildMode_is_idempotent_success()
    {
        var tool = new ImplementPlanTool();
        var context = CreateContext(); // already build mode

        var result = await tool.ExecuteAsync(JsonDocument.Parse("{}").RootElement, context, CancellationToken.None);

        // A redundant call (the approval already flipped to Build) must succeed, not error.
        result.IsError.Should().BeFalse();
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
    public void Plan_tools_are_AutoAllow_and_read_only()
    {
        var input = JsonDocument.Parse("{}").RootElement;
        foreach (var tool in new ToolBase[] { new EnterPlanModeTool(), new CreatePlanTool(), new ImplementPlanTool() })
        {
            tool.RequiredPermission(input).Should().Be(PermissionLevel.AutoAllow);
            tool.IsReadOnly.Should().BeTrue($"{tool.Name} is a mode-control tool and must be callable in plan mode");
        }
    }

    [Fact]
    public async Task CreatePlan_persists_plan_file_and_records_its_path()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "openmono-plan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(workDir);
        try
        {
            var context = CreateContext(workDir);
            context.Session.Meta.PlanMode = true;

            var tool = new CreatePlanTool();
            var input = JsonDocument.Parse("""{"plan": "Step 1: Do the thing"}""").RootElement;
            var result = await tool.ExecuteAsync(input, context, CancellationToken.None);

            result.IsError.Should().BeFalse();
            context.Session.Meta.LastPlanPath.Should().NotBeNull("the plan must be persisted to a file");
            var fullPath = Path.Combine(workDir, context.Session.Meta.LastPlanPath!);
            File.Exists(fullPath).Should().BeTrue();
            (await File.ReadAllTextAsync(fullPath)).Should().Contain("Step 1: Do the thing");
            context.Session.Meta.LastPlanPath!.Replace('\\', '/').Should().StartWith(".openmono/plans/");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    private static ToolContext CreateContext(string? workDir = null)
    {
        workDir ??= Path.GetTempPath();
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
