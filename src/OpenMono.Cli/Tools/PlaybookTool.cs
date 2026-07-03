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

    // Available in both Plan and Build modes, but gated by whether the playbook requires write tools.
    // If a playbook needs write tools in Plan mode, user is prompted to switch to Build mode.
    // This mirrors ImplementPlan's behavior — the tool is available but may require a mode switch.
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
        Console.Error.WriteLine($"[PLAYBOOK_REQCAP] called with input: {input}");
        var playbook = input.TryGetProperty("name", out var nameEl)
            ? _registry.Resolve(nameEl.GetString() ?? "")
            : null;

        Console.Error.WriteLine($"[PLAYBOOK_REQCAP] playbook: {playbook?.Name ?? "NULL"}");

        if (playbook is null)
            return [];

        var plan = _executor.BuildToolPlan(playbook);
        var cap = new PlaybookApproveCap(
            playbook.Name,
            plan.Steps.Select(s => new PlaybookStepInfo(s.Id, s.Gate, s.Description)).ToList(),
            plan.Tools.Select(t => new PlaybookToolInfo(t.Name, t.IsReadOnly, t.Dangerous)).ToList()
        );
        Console.Error.WriteLine($"[PLAYBOOK_REQCAP] returning capability with {cap.Steps.Count} steps");
        return [cap];
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var name = input.GetProperty("name").GetString()!;
        var arguments = input.TryGetProperty("arguments", out var argsEl) ? argsEl.GetString() ?? "" : "";
        var resume = input.TryGetProperty("resume", out var r) && r.GetBoolean();

        var playbook = _registry.Resolve(name);
        if (playbook is null)
        {
            var available = string.Join(", ", _registry.All.Select(p => p.Name));
            return ToolResult.Error($"Playbook '{name}' not found. Available: {available}");
        }

        var plan = _executor.BuildToolPlan(playbook);
        var requiresModeSwitch = context.Session.Meta.PlanMode && PlaybookRequiresWriteTools(playbook, context);
        plan = plan with { RequiresModeSwitch = requiresModeSwitch };

        // Auto-switch from Plan to Build mode if needed
        if (requiresModeSwitch)
        {
            context.Session.Meta.PlanMode = false;
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
        return ToolResult.Success(result);
    }

    private static string FormatPlaybookApprovalPrompt(PlaybookToolPlan plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Playbook '{plan.PlaybookName}' will run these steps:");
        foreach (var step in plan.Steps)
            sb.AppendLine($"  - {step.Id} (gate: {step.Gate})");
        sb.AppendLine();
        var tools = plan.Tools.Count > 0
            ? string.Join(", ", plan.Tools.Select(t => t.Dangerous ? $"{t.Name}*" : t.Name))
            : "(no tools)";
        sb.AppendLine($"Allowed tools: {tools}");
        if (plan.Tools.Any(t => t.Dangerous))
            sb.AppendLine("(* = potentially destructive)");
        if (plan.RequiresModeSwitch)
            sb.AppendLine("Note: this will also switch you from Plan mode to Build mode.");
        sb.Append("Approve running this playbook? [y/N]");
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
