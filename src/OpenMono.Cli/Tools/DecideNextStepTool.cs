using System.Text.Json;
using OpenMono.Decisions;

namespace OpenMono.Tools;

public sealed class DecideNextStepTool : ToolBase
{
    private static readonly string[] FailureMarkers =
    ["failed", "failure", "error", "blocked", "stuck", "denied", "timeout"];

    private readonly DecisionOptions _options;
    private readonly HeuristicBackend _backend;
    private readonly DecisionAudit? _audit;

    public DecideNextStepTool(DecisionOptions options, HeuristicBackend? backend = null, DecisionAudit? audit = null)
    {
        _options = options;
        _backend = backend ?? new HeuristicBackend(options);
        _audit = audit;
    }

    public override string Name => "decide_next_step";

    public override string Description =>
        "Pick the next verdict for a loop: continue, retry, change approach, ask user, or done. Done is conservative.";

    public override bool IsReadOnly => true;

    public override bool IsConcurrencySafe => true;

    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("goal", "Goal the loop works toward")
        .AddString("last_action", "Action just taken")
        .AddString("result", "Result of the last action")
        .AddInteger("attempts", "Attempt count so far", minimum: 0)
        .Require("goal", "last_action", "result", "attempts");

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var goal = input.TryGetProperty("goal", out var g) ? g.GetString() ?? "" : "";
        var result = input.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
        var attempts = input.TryGetProperty("attempts", out var a) && a.ValueKind == JsonValueKind.Number
            ? a.GetInt32()
            : 0;
        if (string.IsNullOrWhiteSpace(goal))
            return ToolResult.InvalidInput("Missing goal.", "Provide goal, last_action, result, attempts.");
        var (verdict, confidence) = Decide(goal, result, attempts);
        var payload = new { verdict, confidence };
        var audit = _audit ?? new DecisionAudit(context.Config);
        await audit.AppendAsync(new DecisionAudit.Entry(
            DateTime.UtcNow.ToString("o"), context.Session.Id,
            "next-step", $"{verdict} {confidence:F2}", 0), ct);
        return ToolResult.Success(JsonSerializer.Serialize(payload, Config.JsonOptions.Default));
    }

    private (string Verdict, double Confidence) Decide(string goal, string result, int attempts)
    {
        if (attempts >= 3)
        {
            var lowered = result.ToLowerInvariant();
            if (FailureMarkers.Any(m => lowered.Contains(m, StringComparison.Ordinal)))
                return ("ask_user", 0.7);
            return ("change_approach", 0.8);
        }
        if (string.IsNullOrWhiteSpace(result))
            return ("retry", 0.6);
        var doneProbe = _backend.JudgeTrue(result, string.Concat(goal, " completed successfully"));
        if (doneProbe >= _options.AutoThreshold)
            return ("done", doneProbe);
        var failProbe = _backend.JudgeTrue(result, "failed with an error");
        if (failProbe >= _options.ReviewThreshold)
            return ("retry", 0.65);
        return ("continue", Math.Max(doneProbe, 0.6));
    }
}
