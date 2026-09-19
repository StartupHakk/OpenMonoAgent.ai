using System.Text;
using System.Text.Json;
using OpenMono.Permissions;
using OpenMono.Playbooks;
using OpenMono.Session;

namespace OpenMono.Tools;

public sealed class PlaybookTool : ToolBase
{
    public override string Name => "Playbook";
    public override string Description => "Invoke a playbook by name. Playbooks are multi-step, typed, composable workflows.";

    public override bool IsDeferred => false;

    // Available in both Plan and Build modes.
    // Marked as read-only so it shows in Plan mode. If it needs write tools, the user is
    // prompted during execution to switch to Build mode (with the plan shown).
    public override bool IsReadOnly => true;

    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    private readonly PlaybookRegistry _registry;
    private readonly PlaybookExecutor _executor;

    public PlaybookTool(PlaybookRegistry registry, PlaybookExecutor executor)
    {
        _registry = registry;
        _executor = executor;
    }

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("name", "Name of the playbook to run")
        .AddString("arguments", "Arguments to pass to the playbook")
        .AddBoolean("resume", "Resume from last checkpoint (default: false)")
        .Require("name");

    public override IReadOnlyList<Capability> RequiredCapabilities(JsonElement input)
    {
        // Playbook handles its own permission flow inside ExecuteCoreAsync
        // (shows plan + permission prompt when switching from Plan to Build mode)
        // No capabilities needed here.
        return [];
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var name = input.GetProperty("name").GetString()!;
        var arguments = input.TryGetProperty("arguments", out var argsEl) ? argsEl.GetString() ?? "" : "";
        var resume = input.TryGetProperty("resume", out var r) && r.GetBoolean();

        Utils.Log.Info($"[PLAYBOOK_EXEC] Starting playbook '{name}', current backend mode: {(context.Session.Meta.PlanMode ? "PLAN" : "BUILD")}");

        var playbook = _registry.Resolve(name);
        if (playbook is null)
        {
            var available = string.Join(", ", _registry.All.Select(p => p.Name));
            return ToolResult.Error($"Playbook '{name}' not found. Available: {available}");
        }

        var plan = _executor.BuildToolPlan(playbook);
        var requiresModeSwitch = context.Session.Meta.PlanMode && PlaybookRequiresWriteTools(playbook, context);
        plan = plan with { RequiresModeSwitch = requiresModeSwitch };
        Utils.Log.Info($"[PLAYBOOK_EXEC] PlaybookRequiresWriteTools={PlaybookRequiresWriteTools(playbook, context)}, requiresModeSwitch={requiresModeSwitch}");

        // In Plan mode with write tools: request permission with the plan shown
        if (requiresModeSwitch && context.Permissions is not null && !playbook.SkipPermissions)
        {
            var summary = FormatPlaybookApprovalPrompt(plan);
            var dangerous = plan.Tools.Any(t => t.Dangerous);
            var cacheKey = $"Playbook:{plan.PlaybookName}";

            Utils.Log.Debug($"[PLAYBOOK] Requesting permission for {plan.PlaybookName} (dangerous={dangerous})");
            var (approved, scope) = await context.Permissions.PauseForUserResponseAsync(
                context.Interaction,
                cacheKey,
                summary,
                dangerous,
                ct
            );

            if (!approved)
                return ToolResult.Error($"Playbook '{plan.PlaybookName}' execution cancelled by user");

            Utils.Log.Debug($"[PLAYBOOK] Permission approved (scope={scope}), switching to Build mode");
        }
        else if (requiresModeSwitch)
        {
            Console.Error.WriteLine($"[PLAYBOOK] skip-permissions enabled — auto-approving mode switch for {plan.PlaybookName}");
        }

        // Auto-switch from Plan to Build mode if approved
        if (requiresModeSwitch)
        {
            context.Session.Meta.PlanMode = false;
            Utils.Log.Info("<---SWITCHED-TO-BUILD-MODE--->");
            Utils.Log.Info($"[PLAYBOOK_EXEC] After mode switch, backend mode is now: {(context.Session.Meta.PlanMode ? "PLAN" : "BUILD")}");
            context.Session.Messages.Add(new OpenMono.Session.Message
            {
                Role = OpenMono.Session.MessageRole.User,
                Content = ModeInstructions.SwitchedToBuild,
            });
        }

        var parameters = ParseArguments(arguments, playbook);

        // Prompt for missing required parameters before executing
        parameters = await PromptMissingParametersAsync(playbook, parameters, context, ct);

        PlaybookState? state = null;
        if (resume)
        {
            state = await PlaybookState.LoadAsync(
                context.Config.DataDirectory, name, context.Session.Id, ct);
        }

        var execResult = await _executor.ExecuteDetailedAsync(playbook, parameters, state, context.Session.Id, ct);
        Utils.Log.Info($"[PLAYBOOK_EXEC] Playbook '{name}' completed, backend mode is: {(context.Session.Meta.PlanMode ? "PLAN" : "BUILD")}");
        if (execResult.Abort is not null)
        {
            // An aborted playbook is a FAILED tool call, not a successful one with bad
            // news inside. The failure (plus ErrorCode) surfaces on ACP tool_end as
            // ok:false + error_code so harnesses can react without parsing prose.
            //
            // The hint is deliberately stop/report, NOT re-run: an abort that reaches this
            // point already exhausted the playbook's own retry-on-abort budget (or never had
            // one). Re-running, resuming, or repairing output by hand here is self-recovery
            // past the budget — report and wait for direction instead. Escalation also ends
            // the turn (see ConversationLoop) so the harness picks the abort up immediately.
            var abort = execResult.Abort;
            var failure = ToolResult.Failure(execResult.Output, abort.ErrorCode,
                $"Playbook '{name}' aborted ({abort.ErrorCode}). Stop: do NOT re-run it, do NOT " +
                $"resume it, and do NOT fix output files by hand. Briefly report what was already " +
                $"completed and wait for direction.");
            // Preserve Tier-3 escalation semantics for nested playbooks: a doom-loop
            // abort inside an inner playbook must abort the outer step too, and any abort
            // escalated here must end the turn.
            if (abort.ErrorCode == PlaybookExecutor.PlaybookAbortCodes.DoomLoopEscalated)
                failure = failure.WithEscalation();
            else
                failure = failure.WithBreakTurn();
            return failure;
        }
        return ToolResult.Success(execResult.Output);
    }

    private static string FormatPlaybookApprovalPrompt(PlaybookToolPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Name of the playbook: {plan.PlaybookName}");
        sb.AppendLine();
        sb.AppendLine("Steps:");
        foreach (var step in plan.Steps)
            sb.AppendLine($"- {step.Id}");
        sb.AppendLine();
        sb.AppendLine("Allowed Tools:");
        if (plan.Tools.Count > 0)
        {
            foreach (var tool in plan.Tools)
            {
                var toolName = tool.Dangerous ? $"{tool.Name} (destructive)" : tool.Name;
                sb.AppendLine($"- {toolName}");
            }
        }
        else
        {
            sb.AppendLine("- (no tools)");
        }

        if (plan.RequiresModeSwitch)
        {
            sb.AppendLine();
            sb.AppendLine("⚠ Note: This playbook requires write access and will switch you from Plan mode to Build mode.");
        }

        return sb.ToString();
    }

    private bool PlaybookRequiresWriteTools(PlaybookDefinition playbook, ToolContext context)
    {
        // Check if the playbook's allowed tools include any non-read-only tools
        var allowedToolNames = playbook.AllowedTools;

        // If playbook allows all tools (*), check if there are any write tools available
        if (allowedToolNames.Contains("*"))
        {
            return context.ToolRegistry.All.Any(t => !t.IsReadOnly);
        }

        // Otherwise, check if any of the playbook's allowed tools are non-read-only
        foreach (var toolName in allowedToolNames)
        {
            var tool = context.ToolRegistry.Resolve(toolName);
            if (tool is not null && !tool.IsReadOnly)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<Dictionary<string, object>> PromptMissingParametersAsync(
        PlaybookDefinition playbook, Dictionary<string, object> parameters, ToolContext context, CancellationToken ct)
    {
        foreach (var (paramName, def) in playbook.Parameters)
        {
            if (parameters.TryGetValue(paramName, out var val) && val is not null)
                continue;

            if (!def.Required)
                continue;

            // Check if the ACP interaction interface is available for prompting
            if (context.Interaction is not null)
            {
                var response = await context.Interaction.RequestUserInputAsync(
                    $"Playbook '{playbook.Name}' requires parameter '{paramName}': {def.Description}",
                    ct);

                if (!string.IsNullOrWhiteSpace(response))
                    parameters[paramName] = response;
            }
            else
            {
                var prompt = $"Playbook '{playbook.Name}' requires parameter '{paramName}': {def.Description}\n> ";
                var response = await context.AskUser(prompt, ct);

                if (!string.IsNullOrWhiteSpace(response))
                    parameters[paramName] = response;
            }
        }

        return parameters;
    }

    private static Dictionary<string, object> ParseArguments(string args, PlaybookDefinition playbook)
        => PlaybookArgParser.ParseArguments(args, playbook);
}
