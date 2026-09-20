using System.Diagnostics;
using System.Text.Json;
using OpenMono.Decisions;

namespace OpenMono.Tools;

public sealed class DecideEvaluateTool : ToolBase
{
    public const int MaxInputChars = 65536;
    public const int MaxStateChars = 8000;

    private readonly DecisionOptions _options;
    private readonly IDecisionBackend _backend;
    private readonly DecisionAudit? _audit;

    public DecideEvaluateTool(DecisionOptions options, IDecisionBackend? backend = null, DecisionAudit? audit = null)
    {
        _options = options;
        _backend = backend ?? DecisionBackendFactory.Create(options);
        _audit = audit;
    }

    public override string Name => "decide_evaluate";

    public override string Description =>
        "Batch typed judgments (choice, score, noul) over one shared state. Returns probabilities plus a code-computed gate.";

    public override bool IsReadOnly => true;

    public override bool IsConcurrencySafe => true;

    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddProperty("state", new { type = "object", description = "Shared evidence, serialized as text for scoring" })
        .AddProperty("questions", new { type = "object", description = "Map of id to {type, instructions, criteria}" })
        .Require("state", "questions");

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        if (input.GetRawText().Length > MaxInputChars)
            return ToolResult.InvalidInput("State is too large.", "Narrow state below 64K chars.");
        if (!input.TryGetProperty("questions", out var questions) || questions.ValueKind != JsonValueKind.Object)
            return ToolResult.InvalidInput("Missing questions object.", "Provide questions as {id: {type, instructions, criteria}}.");
        var stateText = input.TryGetProperty("state", out var state) ? state.GetRawText() : "";
        var truncated = false;
        if (stateText.Length > MaxStateChars)
        {
            stateText = stateText[..MaxStateChars];
            truncated = true;
        }
        var sw = Stopwatch.StartNew();
        var answers = new Dictionary<string, object>();
        foreach (var question in questions.EnumerateObject())
        {
            answers[question.Name] = AnswerOne(question.Value, stateText);
            ct.ThrowIfCancellationRequested();
        }
        sw.Stop();
        var gate = OverallGate(answers.Values.Select(v => ((Answer)v).Gate).ToList());
        var payload = new
        {
            answers = answers.ToDictionary(kv => kv.Key, kv => ((Answer)kv.Value).Payload),
            gate,
            latency_ms = sw.ElapsedMilliseconds,
            truncated,
        };
        var audit = _audit ?? new DecisionAudit(context.Config);
        await audit.AppendAsync(new DecisionAudit.Entry(
            DateTime.UtcNow.ToString("o"), context.Session.Id,
            "evaluate", $"{answers.Count}q gate={gate} {sw.ElapsedMilliseconds}ms", sw.ElapsedMilliseconds), ct);
        return ToolResult.Success(JsonSerializer.Serialize(payload, Config.JsonOptions.Default));
    }

    private Answer AnswerOne(JsonElement question, string stateText)
    {
        var type = question.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        var instructions = question.TryGetProperty("instructions", out var i) ? i.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(instructions))
            return new Answer("escalate", new { error = "missing-type-or-instructions" });
        question.TryGetProperty("criteria", out var criteria);
        return type.ToLowerInvariant() switch
        {
            "choice" => AnswerChoice(stateText, instructions, criteria),
            "score" => AnswerScore(stateText, criteria),
            "noul" => AnswerNoul(stateText, instructions),
            _ => new Answer("escalate", new { error = $"unknown-type:{type}" }),
        };
    }

    private Answer AnswerChoice(string stateText, string instructions, JsonElement criteria)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (criteria.ValueKind == JsonValueKind.Object)
        {
            foreach (var opt in criteria.EnumerateObject())
                options[opt.Name] = opt.Value.ValueKind == JsonValueKind.String ? opt.Value.GetString() : null;
        }
        if (options.Count == 0)
            return new Answer("escalate", new { error = "choice-needs-options" });
        var (choice, confidence, probs) = _backend.Choose(string.Concat(instructions, "\n", stateText), options);
        var gate = DecisionPolicy.ApplyGate(choice, confidence, _options.AutoThreshold, _options.ReviewThreshold);
        return new Answer(gate, new { choice, probabilities = probs, confidence });
    }

    private Answer AnswerScore(string stateText, JsonElement criteria)
    {
        var levels = new List<string>();
        if (criteria.ValueKind == JsonValueKind.Array)
        {
            foreach (var level in criteria.EnumerateArray())
            {
                if (level.ValueKind == JsonValueKind.String && level.GetString() is { } text)
                    levels.Add(text);
            }
        }
        if (levels.Count < 2)
            return new Answer("escalate", new { error = "score-needs-2-plus-levels" });
        var supports = levels.Select(l => _backend.JudgeTrue(stateText, l)).ToList();
        var total = supports.Sum();
        var probs = supports.Select(s => total > 0 ? s / total : 1.0 / levels.Count).ToList();
        var score = probs.Select((p, idx) => p * idx).Sum();
        var confidence = probs.Max();
        var normalized = levels.Count > 1 ? score / (levels.Count - 1) : 0;
        var gate = DecisionPolicy.ScoreBand(normalized, _options.AutoThreshold, _options.ReviewThreshold);
        return new Answer(gate, new { score, legend = levels, probabilities = probs, confidence });
    }

    private Answer AnswerNoul(string stateText, string instructions)
    {
        var p = _backend.JudgeTrue(stateText, instructions);
        var gate = DecisionPolicy.NoulGate(p, _options.AutoThreshold, _options.ReviewThreshold);
        return new Answer(gate, new { noul = p });
    }

    private static string OverallGate(IReadOnlyList<string> gates)
    {
        if (gates.Any(g => g == "escalate"))
            return "escalate";
        if (gates.Any(g => g == "review"))
            return "review";
        return "auto";
    }

    private sealed record Answer(string Gate, object Payload);
}
