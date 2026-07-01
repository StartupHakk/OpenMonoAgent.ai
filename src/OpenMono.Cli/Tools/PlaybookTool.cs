using System.Text.Json;
using OpenMono.Permissions;
using OpenMono.Playbooks;

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

    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input) => [];

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

        // Check if playbook needs non-read-only tools and we're in Plan mode
        if (context.Session.Meta.PlanMode && PlaybookRequiresWriteTools(playbook, context))
        {
            // Use ACP interaction (RequestToggleModeAsync) if available, otherwise fall back to AskUser
            if (context.Interaction is not null)
            {
                var approved = await context.Interaction.RequestToggleModeAsync(
                    $"Playbook '{playbook.Name}' requires Build mode (write tools)",
                    ct);

                if (!approved)
                {
                    return ToolResult.Error($"Playbook '{name}' requires Build mode. User declined to switch.");
                }

                // Switch to Build mode (will be re-executed by ResumeWithToggleModeAsync)
                context.Session.Meta.PlanMode = false;
            }
            else
            {
                // TUI fallback: ask user synchronously
                var userResponse = await context.AskUser(
                    $"The playbook '{playbook.Name}' requires Build mode (write tools). Switch to Build mode? [yes/no]",
                    ct);

                if (userResponse?.Equals("yes", StringComparison.OrdinalIgnoreCase) != true)
                {
                    return ToolResult.Error($"Playbook '{name}' requires Build mode. User declined to switch.");
                }

                // Switch to Build mode
                context.Session.Meta.PlanMode = false;
            }
        }

        var parameters = ParseArguments(arguments, playbook);

        PlaybookState? state = null;
        if (resume)
        {
            state = await PlaybookState.LoadAsync(
                context.Config.DataDirectory, name, context.Session.Id, ct);
        }

        var result = await _executor.ExecuteAsync(playbook, parameters, state, ct);
        return ToolResult.Success(result);
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
