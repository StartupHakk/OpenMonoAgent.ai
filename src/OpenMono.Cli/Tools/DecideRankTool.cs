using System.Diagnostics;
using System.Text.Json;
using OpenMono.Decisions;

namespace OpenMono.Tools;

public sealed class DecideRankTool : ToolBase
{
    public const int MaxCandidates = 500;
    public const int ChunkSize = 50;

    private readonly DecisionOptions _options;
    private readonly HeuristicBackend _backend;

    public DecideRankTool(DecisionOptions options, HeuristicBackend? backend = null)
    {
        _options = options;
        _backend = backend ?? new HeuristicBackend(options);
    }

    public override string Name => "decide_rank";

    public override string Description =>
        "Rank candidates by relevance to a query. Returns ordered ids with scores.";

    public override bool IsReadOnly => true;

    public override bool IsConcurrencySafe => true;

    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("query", "Query to rank candidates against")
        .AddProperty("candidates", new
        {
            type = "array",
            description = "Candidates as [{id, text}]",
            items = new { type = "object" },
        })
        .AddProperty("min_relevance", new { type = "number", description = "Minimum relevance kept, default 0.5" })
        .Require("query", "candidates");

    protected override Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        if (!input.TryGetProperty("query", out var queryEl) || queryEl.GetString() is not { } query)
            return Task.FromResult(ToolResult.InvalidInput("Missing query.", "Provide query and candidates."));
        if (!input.TryGetProperty("candidates", out var candidatesEl) || candidatesEl.ValueKind != JsonValueKind.Array)
            return Task.FromResult(ToolResult.InvalidInput("Missing candidates array.", "Provide candidates as [{id, text}]."));
        var minRelevance = _options.MinConfidence;
        if (input.TryGetProperty("min_relevance", out var minEl) && minEl.ValueKind == JsonValueKind.Number)
            minRelevance = Math.Clamp(minEl.GetDouble(), 0, 1);
        var candidates = new List<(string Id, string Text)>();
        foreach (var candidate in candidatesEl.EnumerateArray())
        {
            if (candidate.TryGetProperty("id", out var id) && id.GetString() is { } cid &&
                candidate.TryGetProperty("text", out var text) && text.GetString() is { } ctext)
                candidates.Add((cid, ctext));
            else
                return Task.FromResult(ToolResult.InvalidInput("Malformed candidate.", "Each candidate needs id and text."));
        }
        if (candidates.Count > MaxCandidates)
            return Task.FromResult(ToolResult.InvalidInput($"Too many candidates ({candidates.Count}).", "Keep at most 500 per call."));
        var sw = Stopwatch.StartNew();
        var scored = new List<(string Id, double Relevance)>(candidates.Count);
        foreach (var chunk in candidates.Chunk(ChunkSize))
        {
            foreach (var (id, text) in chunk)
                scored.Add((id, _backend.Relevance(query, text)));
            ct.ThrowIfCancellationRequested();
        }
        sw.Stop();
        var ranked = scored
            .Where(s => s.Relevance >= minRelevance)
            .OrderByDescending(s => s.Relevance)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .Select(s => new { id = s.Id, relevance = s.Relevance })
            .ToList();
        var payload = new
        {
            ranked,
            any_relevant = ranked.Count > 0,
            latency_ms = sw.ElapsedMilliseconds,
        };
        return Task.FromResult(ToolResult.Success(JsonSerializer.Serialize(payload, Config.JsonOptions.Default)));
    }
}
