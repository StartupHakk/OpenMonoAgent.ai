using System.Text.Json;
using OpenMono.Session;

namespace OpenMono.Tools;

public sealed class EnterPlanModeTool : ToolBase
{
    public override string Name => "EnterPlanMode";
    public override string Description =>
        """
        Use this tool proactively before starting any non-trivial implementation task.
        Getting user sign-off on your approach before writing code prevents wasted effort.

        ## When to use EnterPlanMode

        Use it when ANY of these apply:

        - New feature that involves architectural decisions (where does it go? what pattern?)
        - Multiple valid approaches exist and the choice meaningfully affects the codebase
        - Changes that touch more than 2-3 files
        - Unclear requirements — you need to explore before you can understand the scope
        - High-impact restructuring where the wrong approach causes significant rework
        - You would normally ask a clarifying question about the approach — plan instead

        ## When NOT to use EnterPlanMode

        Skip it for simple tasks:
        - Single-line or few-line fixes, typos, obvious bugs
        - The user gave specific, detailed instructions and the path is clear
        - Pure research/exploration (use the Agent tool with Explore type instead)
        - The user said "just do it" or "go ahead" — start working
        """;

    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;
    public override bool IsReadOnly => true;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("reason", "Why you are entering plan mode — what task are you planning?")
        .Require("reason");

    protected override Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var reason = input.GetProperty("reason").GetString()!;

        if (context.Session.Meta.PlanMode)
            return Task.FromResult(ToolResult.Error(
                "Already in plan mode. Investigate, then call CreatePlan to present your plan."));

        context.Session.Meta.PlanMode = true;
        Utils.Log.Info("<---SWITCHED-TO-PLAN-MODE--->");
        return Task.FromResult(ToolResult.Success(ModeInstructions.Activation(reason)));
    }
}

/// <summary>
/// Presents a completed plan to the user. Stays in Plan mode — the plan is for review,
/// not yet implementation. The user approves, then the agent calls ImplementPlan to switch
/// to Build mode and execute. Replaces the old ExitPlanMode, which conflated "present the
/// plan" with "drop to Build" and let the agent start writing before the user approved.
/// </summary>
public sealed class CreatePlanTool : ToolBase
{
    public override string Name => "CreatePlan";
    public override string Description =>
        """
        Present your completed implementation plan to the user for approval.
        Call this when your plan is ready. You STAY in Plan mode (read-only) — this only
        presents the plan. After the user approves, call ImplementPlan to switch to Build
        mode and execute. Do NOT start writing files from this tool.

        The `plan` argument must be a structured numbered plan — not vague prose.
        It should list: the approach, every file that changes, risks, and complexity.
        """;

    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;
    public override bool IsReadOnly => true;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("plan", "The full numbered implementation plan to present to the user")
        .Require("plan");

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var plan = input.GetProperty("plan").GetString()!;

        if (!context.Session.Meta.PlanMode)
            return ToolResult.Error(
                "Not in plan mode — call EnterPlanMode first to plan, then CreatePlan to present it.");

        context.Session.Meta.LastPlanContent = plan;

        // Persist the plan to a discoverable plans/ directory. CreatePlan writes it server-side
        // (control tool), so plan mode stays read-only for the agent's own tools — no FileWrite
        // gate carve-out. The saved file is reviewable/editable and feeds later implementation.
        var savedPath = await TryWritePlanFileAsync(plan, context, ct);
        context.Session.Meta.LastPlanPath = savedPath;

        // The user sees the formatted plan + option buttons in the plan_ready card (extension)
        // / the loop renderer (TUI). This result is a brief agent-facing status; the "wait,
        // then ImplementPlan" instruction comes from the PlanPresented message.
        return ToolResult.Success("Plan presented to the user — awaiting their choice (Auto implement / Ask before edits / Keep planning).")
            .WithBreakTurn();
    }

    // Writes the plan under <workspace>/.openmono/plans/ with a timestamped filename so
    // revisions are kept. Best-effort: a write failure must not break plan presentation.
    private static async Task<string?> TryWritePlanFileAsync(string plan, ToolContext context, CancellationToken ct)
    {
        try
        {
            var dir = Path.Combine(context.WorkingDirectory, ".openmono", "plans");
            Directory.CreateDirectory(dir);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var path = Path.Combine(dir, $"{stamp}-plan.md");
            var content = $"# Plan — {stamp} UTC\n\nSession: {context.Session.Id}\n\n{plan}\n";
            await File.WriteAllTextAsync(path, content, ct);
            return Path.GetRelativePath(context.WorkingDirectory, path);
        }
        catch (Exception ex)
        {
            context.OnDebug?.Invoke($"[CreatePlan] failed to persist plan file: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// Switches from Plan to Build mode to implement the approved plan. Call only after the
/// user has approved. Build tools become available on the same turn (the loop rebuilds the
/// tool set after the mode flip), and the flip is pushed to the UI/TUI automatically.
/// </summary>
public sealed class ImplementPlanTool : ToolBase
{
    public override string Name => "ImplementPlan";
    public override string Description =>
        """
        Switch to Build mode and implement the plan the user just approved.
        Call this ONLY after the user has approved the plan presented by CreatePlan.
        After calling it you have full tool access — proceed to implement the plan.
        """;

    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;
    public override bool IsReadOnly => true;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder();

    protected override Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var plan = context.Session.Meta.LastPlanContent is { Length: > 0 } p ? $"\n\nThe approved plan:\n{p}" : "";

        // Idempotent: when the approval already flipped to Build (deterministic plan_decision
        // routing), a redundant ImplementPlan call must SUCCEED, not error — just proceed.
        if (!context.Session.Meta.PlanMode)
            return Task.FromResult(ToolResult.Success(
                "Already in Build mode with full tool access. Implement the approved plan now." + plan));

        context.Session.Meta.PlanMode = false;
        Utils.Log.Info("<---SWITCHED-TO-BUILD-MODE--->");
        return Task.FromResult(ToolResult.Success(
            "Switched to Build mode — you now have full tool access (FileWrite, FileEdit, Bash, etc.). " +
            "Implement the approved plan now." + plan));
    }
}
