using System.Diagnostics;
using System.Text.Json;
using OpenMono.Decisions;

namespace OpenMono.Tools;

public sealed class DecideVerifyTool : ToolBase
{
    public const int MaxClaims = 100;

    private readonly DecisionOptions _options;
    private readonly IDecisionBackend _backend;
    private readonly DecisionAudit? _audit;

    public DecideVerifyTool(DecisionOptions options, IDecisionBackend? backend = null, DecisionAudit? audit = null)
    {
        _options = options;
        _backend = backend ?? DecisionBackendFactory.Create(options);
        _audit = audit;
    }

    public override string Name => "decide_verify";

    public override string Description =>
        "Check claims against evidence with closed-world semantics. Absent means not addressed.";

    public override bool IsReadOnly => true;

    public override bool IsConcurrencySafe => true;

    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("evidence", "Evidence text claims are checked against")
        .AddProperty("claims", new
        {
            type = "array",
            description = "Claims to check, at most 100",
            items = new { type = "string" },
        })
        .Require("evidence", "claims");

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        if (!input.TryGetProperty("evidence", out var evidenceEl) || evidenceEl.GetString() is not { } evidence)
            return ToolResult.InvalidInput("Missing evidence.", "Provide evidence and claims.");
        if (!input.TryGetProperty("claims", out var claimsEl) || claimsEl.ValueKind != JsonValueKind.Array)
            return ToolResult.InvalidInput("Missing claims array.", "Provide claims as [string].");
        var claims = new List<string>();
        foreach (var claim in claimsEl.EnumerateArray())
        {
            if (claim.ValueKind == JsonValueKind.String && claim.GetString() is { } text)
                claims.Add(text);
            else
                return ToolResult.InvalidInput("Malformed claim.", "Each claim must be a string.");
        }
        if (claims.Count > MaxClaims)
            return ToolResult.InvalidInput($"Too many claims ({claims.Count}).", "Keep at most 100 per call.");
        var sw = Stopwatch.StartNew();
        var verdicts = new List<object>(claims.Count);
        var allSupported = true;
        var supported = 0;
        foreach (var claim in claims)
        {
            var (verdict, confidence) = _backend.Verify(evidence, claim);
            if (verdict != "supported")
                allSupported = false;
            else
                supported++;
            verdicts.Add(new { claim, verdict, confidence });
            ct.ThrowIfCancellationRequested();
        }
        sw.Stop();
        var payload = new
        {
            verdicts,
            all_supported = allSupported,
            latency_ms = sw.ElapsedMilliseconds,
        };
        var audit = _audit ?? new DecisionAudit(context.Config);
        await audit.AppendAsync(new DecisionAudit.Entry(
            DateTime.UtcNow.ToString("o"), context.Session.Id,
            "verify", $"{claims.Count}c supported={supported} {sw.ElapsedMilliseconds}ms", sw.ElapsedMilliseconds), ct);
        return ToolResult.Success(JsonSerializer.Serialize(payload, Config.JsonOptions.Default));
    }
}
