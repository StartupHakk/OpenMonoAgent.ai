using System.Text.Json;
using OpenMono.Permissions;

namespace OpenMono.Tools;

public sealed class EnterPlanModeTool : ToolBase
{
    public override string Name => "EnterPlanMode";
    public override string Description => "Enter plan mode to decompose a complex task into steps before implementing. In plan mode, only read-only tools are available.";
    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    public override bool IsReadOnly => true;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("reason", "Why you are entering plan mode")
        .Require("reason");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input) => [];

    protected override Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var reason = input.GetProperty("reason").GetString()!;

        if (context.Session.Meta.PlanMode)
            return Task.FromResult(ToolResult.Error("Already in plan mode. Use ExitPlanMode to leave."));

        context.Session.Meta.PlanMode = true;

        return Task.FromResult(ToolResult.Success(
            $"Entered plan mode: {reason}\n\n" +
            "You are now in plan mode. Focus on:\n" +
            "1. Analyzing the codebase (use FileRead, Glob, Grep)\n" +
            "2. Breaking the task into concrete steps\n" +
            "3. Identifying files that need changes\n" +
            "4. Estimating complexity and risks\n\n" +
            "Use ExitPlanMode when your plan is ready to present."));
    }
}

public sealed class ExitPlanModeTool : ToolBase
{
    public override string Name => "ExitPlanMode";
    public override string Description => "Exit plan mode and return to normal execution. Present your plan to the user.";
    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    public override bool IsReadOnly => true;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("plan", "The plan to present to the user")
        .Require("plan");

    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input) => [];

    protected override Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var plan = input.GetProperty("plan").GetString()!;

        if (!context.Session.Meta.PlanMode)
            return Task.FromResult(ToolResult.Error("Not in plan mode. Use EnterPlanMode first."));

        context.Session.Meta.PlanMode = false;

        return Task.FromResult(ToolResult.Success(
            $"Exited plan mode. Here is the plan:\n\n{plan}"));
    }
}
