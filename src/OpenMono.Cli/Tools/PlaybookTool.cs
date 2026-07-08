using System.Text;
using System.Text.Json;
using OpenMono.Permissions;
using OpenMono.Playbooks;
using OpenMono.Session;

namespace OpenMono.Tools;

public sealed class PlaybookTool : ToolBase
{
    public override string Name => "Playbook";
    public override string Description => "Delegate a task to a specialist agent persona. 257 expert playbooks are available covering security, architecture, design, engineering, marketing, product, game-dev, and more. ALWAYS prefer delegating to a specialist over doing the work yourself — see OPENMONO.md for the routing table. Pass the playbook name and a detailed task description as arguments.";

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
        if (requiresModeSwitch && context.Permissions is not null)
        {
            var summary = FormatPlaybookApprovalPrompt(plan);
            var dangerous = plan.Tools.Any(t => t.Dangerous);
            var cacheKey = $"Playbook:{plan.PlaybookName}";

            Console.Error.WriteLine($"[PLAYBOOK] Requesting permission for {plan.PlaybookName} (dangerous={dangerous})");
            var (approved, scope) = await context.Permissions.PauseForUserResponseAsync(
                context.Interaction,
                cacheKey,
                summary,
                dangerous,
                ct
            );

            if (!approved)
                return ToolResult.Error($"Playbook '{plan.PlaybookName}' execution cancelled by user");

            Console.Error.WriteLine($"[PLAYBOOK] Permission approved (scope={scope}), switching to Build mode");
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

        var result = await _executor.ExecuteAsync(playbook, parameters, state, context.Session.Id, ct);
        Utils.Log.Info($"[PLAYBOOK_EXEC] Playbook '{name}' completed, backend mode is: {(context.Session.Meta.PlanMode ? "PLAN" : "BUILD")}");
        return ToolResult.Success(result);
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
    {
        var result = new Dictionary<string, object>();
        if (string.IsNullOrWhiteSpace(args)) return result;

        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].StartsWith("--") && parts[i].Contains('='))
            {
                var kv = parts[i][2..].Split('=', 2);
                result[kv[0]] = kv[1];
            }
            else if (parts[i].StartsWith("--") && i + 1 < parts.Length)
            {
                result[parts[i][2..]] = parts[i + 1];
                i++;
            }
            else if (!result.ContainsKey("_positional"))
            {

                var firstParam = playbook.Parameters.FirstOrDefault(p => p.Value.Required);
                if (firstParam.Key is not null)
                    result[firstParam.Key] = parts[i];
                else
                    result["_positional"] = parts[i];
            }
        }

        return result;
    }
}
