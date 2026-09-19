using System.Text.Json;
using OpenMono.Config;
using OpenMono.Decisions;
using OpenMono.Permissions;
using OpenMono.Tools;

namespace OpenMono.Commands;

public sealed class DecisionCommand : ICommand
{
    public string Name => "decision";

    public string Description => "Local decision layer: status, ask, rank, verify, gate, chief, claim, done, queue";

    public CommandType Type => CommandType.Local;

    public async Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        args = Tokenize(string.Join(" ", args));
        if (args.Length == 0)
        {
            context.Renderer.WriteError("Usage: /decision status|ask|rank|verify|gate|chief|claim|done|queue …");
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
            case "claim":
            case "done":
                ClaimOrDone(args, context);
                break;
            case "queue":
                ListQueue(args, context);
                break;
            default:
                context.Renderer.WriteError($"Unknown /decision subcommand '{args[0]}'. Use status|ask|rank|verify|gate|chief|claim|done|queue.");
                break;
        }
    }

    internal static string StatusText(DecisionSettings settings) =>
        string.Concat(
            "Decision layer (local): ", settings.Enabled ? "enabled" : "disabled",
            "\nauto_threshold=", settings.AutoThreshold.ToString("F2"),
            " review_threshold=", settings.ReviewThreshold.ToString("F2"),
            " min_confidence=", settings.MinConfidence.ToString("F2"),
            " max_steps=", settings.MaxSteps.ToString(),
            " force_ask=", settings.ForceAskPatterns.Count.ToString());

    private static void ClaimOrDone(string[] args, CommandContext context)
    {
        var claiming = args[0].Equals("claim", StringComparison.OrdinalIgnoreCase); var flags = ParseFlags(args[1..]);
        if (!flags.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path)) { context.Renderer.WriteError(claiming ? "Usage: /decision claim --path <file>" : "Usage: /decision done --path <file>"); return; }
        var handoff = claiming ? JobHandoff.TryClaim(path) : JobHandoff.MarkDone(path);
        if (handoff is null)
            context.Renderer.WriteError($"No handoff at '{path}'.");
        else
            context.Renderer.WriteInfo($"{handoff.Status}: {handoff.Choice} confidence={handoff.Confidence:F2} destination={handoff.Destination}");
    }

    private static void ListQueue(string[] args, CommandContext context)
    {
        var flags = ParseFlags(args[1..]);
        if (!flags.TryGetValue("dest", out var dest) || dest is not ("research" or "write" or "review")) { context.Renderer.WriteError("Usage: /decision queue --dest <research|write|review> [--max <n>]"); return; }
        var max = flags.TryGetValue("max", out var maxRaw) && int.TryParse(maxRaw, out var parsed) && parsed > 0 ? Math.Min(parsed, 200) : 200;
        var handoffs = JobHandoff.List(context.WorkingDirectory, dest, max);
        foreach (var handoff in handoffs)
            context.Renderer.WriteInfo($"{handoff.Status} {handoff.Uuid} {handoff.Choice} {handoff.Confidence:F2}");
        if (handoffs.Count == 0)
            context.Renderer.WriteInfo("Queue empty.");
    }

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
            context.Renderer.WriteError("Usage: /decision chief --goal \"<goal>\" --notes \"<notes>\" [--threshold <0..1>]");
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

    internal static string[] Tokenize(string raw)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var ch in raw)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return [.. tokens];
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
