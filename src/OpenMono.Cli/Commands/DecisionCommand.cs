using System.Text.Json;
using OpenMono.Config;
using OpenMono.Decisions;
using OpenMono.Permissions;
using OpenMono.Tools;

namespace OpenMono.Commands;

public sealed class DecisionCommand : ICommand
{
    public string Name => "decision";

    public string Description => "Local decision layer: status, ask, rank, verify, gate, chief";

    public CommandType Type => CommandType.Local;

    public async Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            context.Renderer.WriteError("Usage: /decision status|ask|rank|verify|gate|chief …");
            return;
        }
        var options = DecisionOptions.FromSettings(context.Config.Decision);
        switch (args[0].ToLowerInvariant())
        {
            case "status":
                context.Renderer.WriteInfo(StatusText(context.Config.Decision));
                break;
            case "ask":
                await RunToolAsync(new DecideEvaluateTool(options), JsonArgs(args), context, ct);
                break;
            case "rank":
                await RunToolAsync(new DecideRankTool(options), JsonArgs(args), context, ct);
                break;
            case "verify":
                await RunToolAsync(new DecideVerifyTool(options), JsonArgs(args), context, ct);
                break;
            case "gate":
                await RunToolAsync(
                    new DecisionGateTool(options, context.WorkingDirectory), JsonArgs(args), context, ct);
                break;
            case "chief":
                await RunChiefAsync(options, args[1..], context, ct);
                break;
            default:
                context.Renderer.WriteError($"Unknown /decision subcommand '{args[0]}'. Use status|ask|rank|verify|gate|chief.");
                break;
        }
    }

    internal static string StatusText(DecisionSettings settings) =>
        string.Concat(
            "Decision layer (local): ", settings.Enabled ? "enabled" : "disabled",
            "\nauto_threshold=", settings.AutoThreshold.ToString("F2"),
            " review_threshold=", settings.ReviewThreshold.ToString("F2"),
            " min_confidence=", settings.MinConfidence.ToString("F2"),
            " max_steps=", settings.MaxSteps.ToString());

    private static string JsonArgs(string[] args) => string.Join(" ", args[1..]);

    private static async Task RunToolAsync(ToolBase tool, string json, CommandContext context, CancellationToken ct)
    {
        JsonElement input;
        try
        {
            input = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException ex)
        {
            context.Renderer.WriteError($"Invalid JSON: {ex.Message}");
            return;
        }
        var toolContext = new ToolContext
        {
            ToolRegistry = context.ToolRegistry,
            Session = context.Session,
            Permissions = new PermissionEngine(context.Config, context.Renderer, context.Renderer),
            Config = context.Config,
            WorkingDirectory = context.WorkingDirectory,
            WriteOutput = _ => { },
            AskUser = (q, token) => context.Renderer.AskUserAsync(q, token),
        };
        var result = await tool.ExecuteAsync(input, toolContext, ct);
        context.Renderer.WriteInfo(result.Content);
    }

    private static async Task RunChiefAsync(
        DecisionOptions options, string[] args, CommandContext context, CancellationToken ct)
    {
        var flags = ParseFlags(args);
        if (!flags.TryGetValue("goal", out var goal) || string.IsNullOrWhiteSpace(goal))
        {
            context.Renderer.WriteError("Usage: /decision chief --goal <goal> --notes <notes> [--threshold <0..1>]");
            return;
        }
        flags.TryGetValue("notes", out var notes);
        double? threshold = null;
        if (flags.TryGetValue("threshold", out var thresholdRaw) && double.TryParse(thresholdRaw, out var parsed))
            threshold = Math.Clamp(parsed, 0, 1);
        var router = new ChiefRouter(options, context.WorkingDirectory);
        var (handoff, path) = await router.RouteAsync(goal, notes ?? "", threshold, ct);
        context.Renderer.WriteInfo($"Saved handoff: {path}");
        context.Renderer.WriteInfo($"choice={handoff.Choice} confidence={handoff.Confidence:F2} destination={handoff.Destination}");
    }

    internal static Dictionary<string, string> ParseFlags(string[] args)
    {
        var flags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var collected = new List<string>();
        void Flush()
        {
            if (current is not null)
                flags[current] = string.Join(" ", collected);
            collected.Clear();
        }
        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                Flush();
                current = arg[2..];
            }
            else if (current is not null)
            {
                collected.Add(arg);
            }
        }
        Flush();
        return flags;
    }
}
