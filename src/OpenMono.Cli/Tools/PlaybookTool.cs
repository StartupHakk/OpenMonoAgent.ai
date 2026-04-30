using System.Text.Json;
using OpenMono.Permissions;
using OpenMono.Playbooks;

namespace OpenMono.Tools;

public sealed class PlaybookTool : ToolBase
{
    public override string Name => "Playbook";
    public override string Description => "Invoke a playbook by name. Playbooks are multi-step, typed, composable workflows.";

    public override bool IsDeferred => false;

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
