using System.Text.Json;
using OpenMono.Decisions;

namespace OpenMono.Tools;

public sealed class DecideNextStepTool : ToolBase
{
    private static readonly string[] FailureMarkers =
    ["failed", "failure", "error", "blocked", "stuck", "denied", "timeout"];

    private readonly DecisionOptions _options;
    private readonly IDecisionBackend _backend;
    private readonly DecisionAudit? _audit;

    public DecideNextStepTool(DecisionOptions options, IDecisionBackend? backend = null, DecisionAudit? audit = null)
    {
        _options = options;
        _backend = backend ?? DecisionBackendFactory.Create(options);
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
        .AddString("mode", "Loop verdict (default) or route to a tool")
        .AddString("choice", "Explicit route choice validated against the tool menu")
        .AddString("include_tools", "Comma-separated globs keeping route tools")
        .AddString("exclude_tools", "Comma-separated globs removing route tools")
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
        if (input.TryGetProperty("mode", out var modeEl) && modeEl.ValueKind == JsonValueKind.String && modeEl.GetString() == "route") return await RouteAsync(input, context, string.Concat(goal, "\n", result), ct);
        var (verdict, confidence) = Decide(goal, result, attempts);
        var payload = new { verdict, confidence };
        var audit = _audit ?? new DecisionAudit(context.Config);
        await audit.AppendAsync(new DecisionAudit.Entry(
            DateTime.UtcNow.ToString("o"), context.Session.Id,
            "next-step", $"{verdict} {confidence:F2}", 0), ct);
        return ToolResult.Success(JsonSerializer.Serialize(payload, Config.JsonOptions.Default));
    }

    private async Task<ToolResult> RouteAsync(JsonElement input, ToolContext context, string state, CancellationToken ct)
    {
        var names = context.ToolRegistry.All.Select(t => (t.Name, (string?)t.Description)).ToList();
        IReadOnlyDictionary<string, string?> menu;
        try { menu = ChoiceMenu.Build("route", ChoiceMenu.Filter(names, InputText(input, "include_tools"), InputText(input, "exclude_tools"))); }
        catch (InvalidOperationException ex) { return ToolResult.InvalidInput(ex.Message, "Narrow menu with include_tools or exclude_tools."); }
        var wanted = InputText(input, "choice"); string pick = ChoiceMenu.OtherKey; double confidence = 0.5;
        if (!string.IsNullOrWhiteSpace(wanted) && menu.ContainsKey(wanted)) { pick = wanted; confidence = 0.95; }
        else if (string.IsNullOrWhiteSpace(wanted)) { (pick, confidence, _) = _backend.Choose(state, menu); }
        var choice = pick == ChoiceMenu.OtherKey ? "ask_user" : pick;
        var audit = _audit ?? new DecisionAudit(context.Config);
        await audit.AppendAsync(new DecisionAudit.Entry(DateTime.UtcNow.ToString("o"), context.Session.Id, "route", choice, 0), ct);
        return ToolResult.Success(JsonSerializer.Serialize(new { choice, confidence = choice == pick ? confidence : 0.5 }, Config.JsonOptions.Default));
    }
    private static string? InputText(JsonElement input, string name) => input.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    private (string Verdict, double Confidence) Decide(string goal, string result, int attempts)
    {
        if (attempts >= 3)
        {
            if (DecisionText.ContainsAnyPhrase(result, FailureMarkers))
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
