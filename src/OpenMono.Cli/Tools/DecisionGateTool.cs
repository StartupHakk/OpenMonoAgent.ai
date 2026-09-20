using System.Text.Json;
using OpenMono.Decisions;
using OpenMono.Permissions;
using OpenMono.Utils;

namespace OpenMono.Tools;

public sealed class DecisionGateTool : ToolBase
{
    public sealed record GateSignals(bool Destructive, bool OutwardFacing, bool InScope, int BlastRadius);

    public sealed record DecisionGateResult(
        string Decision,
        double Confidence,
        IReadOnlyList<string> Reasons,
        GateSignals Signals);

    private static readonly string[] WipeMarkers =
    [
        "rm -rf /", "rm -rf /*", "rm -rf ~", "mkfs", ":(){:|:&};:",
        "dd if=/dev/zero of=/dev/", "dd if=/dev/random of=/dev/",
    ];

    private readonly DecisionOptions _options;
    private readonly string _workingDirectory;
    private readonly IDecisionBackend _backend;
    private readonly DecisionAudit? _audit;

    public DecisionGateTool(
        DecisionOptions options, string workingDirectory,
        IDecisionBackend? backend = null, DecisionAudit? audit = null)
    {
        _options = options;
        _workingDirectory = workingDirectory;
        _backend = backend ?? DecisionBackendFactory.Create(options);
        _audit = audit;
    }

    public override string Name => "decide_gate_action";

    public override string Description =>
        "Advisory gate for a proposed writable action. Returns allow, confirm, or block with signals. Never a security boundary.";

    public override bool IsReadOnly => true;

    public override bool IsConcurrencySafe => true;

    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("tool", "Tool name, one of Bash, FileWrite, FileEdit, ApplyPatch")
        .AddProperty("args", new { type = "object", description = "Tool arguments" })
        .AddString("user_request", "Original user request for context")
        .Require("tool", "args", "user_request");

    public DecisionGateResult Check(string toolName, string toolArgsJson, string userRequest)
    {
        JsonElement args;
        try
        {
            // Single parse per gate call: the element is threaded through every
            // helper below so SignalsFor never re-parses the raw JSON.
            using var doc = JsonDocument.Parse(toolArgsJson);
            args = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return Confirm(["unparseable-args"], 0.5, toolName, null, userRequest);
        }
        if (!DecisionFastPaths.ShouldConsult(toolName))
            return ApplyForceAsk(Allow("ungated-tool", toolName, args), toolName, toolArgsJson, args);
        if (DecisionFastPaths.TryAllow(toolName, args, _workingDirectory, out var fastReason))
            return ApplyForceAsk(Allow(fastReason, toolName, args), toolName, toolArgsJson, args);
        var destructive = DecisionFastPaths.IsDestructive(toolName, args);
        if (IsWipe(toolName, args))
            return Block("wipe-pattern", toolName, args);
        if (ExfiltratesSecrets(toolName, args))
            return Block("secret-egress", toolName, args);
        if (!destructive)
            return ApplyForceAsk(Allow("heuristic-benign", toolName, args), toolName, toolArgsJson, args);
        return Confirm("destructive", destructive ? 0.8 : 0.65, toolName, args, userRequest);
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var tool = input.TryGetProperty("tool", out var t) ? t.GetString() ?? "" : "";
        var args = input.TryGetProperty("args", out var a) ? a.GetRawText() : "{}";
        var userRequest = input.TryGetProperty("user_request", out var u) ? u.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(tool))
            return ToolResult.InvalidInput("Missing tool name.", "Provide tool, args, user_request.");
        var result = Check(tool, args, userRequest);
        var audit = _audit ?? new DecisionAudit(context.Config);
        // The gate decision itself never depends on the audit write, but an
        // unwritable audit file must be visible, not silent: surface it in
        // the payload so the caller (and journal) can record audit=failed.
        var auditOk = await audit.AppendAsync(new DecisionAudit.Entry(
            DateTime.UtcNow.ToString("o"), context.Session.Id,
            "gate", $"{tool} {result.Decision} {result.Confidence:F2}", 0,
            "local-heuristic", "local-heuristic", $"{tool}->{result.Decision}"), ct);
        var payload = new
        {
            decision = result.Decision,
            confidence = result.Confidence,
            reasons = result.Reasons,
            audit = auditOk ? "ok" : "failed",
            signals = new
            {
                destructive = result.Signals.Destructive,
                outward_facing = result.Signals.OutwardFacing,
                in_scope = result.Signals.InScope,
                blast_radius = result.Signals.BlastRadius,
            },
        };
        var json = JsonSerializer.Serialize(payload, Config.JsonOptions.Default);
        return ToolResult.Success(json);
    }

    private DecisionGateResult Allow(string reason, string toolName, JsonElement? args) =>
        new("allow", 0.99, [reason], SignalsFor(toolName, args, 0));

    private DecisionGateResult Block(string reason, string toolName, JsonElement? args) =>
        new("block", 0.9, [reason], SignalsFor(toolName, args, 3));

    private DecisionGateResult Confirm(
        string reason, double confidence, string toolName, JsonElement? args, string userRequest)
    {
        var risk = _backend.JudgeTrue(
            string.Concat(userRequest, "\n", toolName, " ", args?.GetRawText() ?? "{}"),
            "this action risks data loss or exfiltration");
        var conf = risk >= _options.ReviewThreshold ? Math.Max(confidence, 0.8) : confidence;
        return new("confirm", conf, [reason], SignalsFor(toolName, args, 2));
    }

    private DecisionGateResult Confirm(IReadOnlyList<string> reasons, double confidence, string toolName, JsonElement? args, string userRequest) =>
        new("confirm", confidence, reasons, SignalsFor(toolName, args, 2));

    private DecisionGateResult ApplyForceAsk(DecisionGateResult result, string toolName, string argsJson, JsonElement args)
    {
        if (!string.Equals(result.Decision, "allow", StringComparison.Ordinal) || _options.ForceAskPatterns.Count == 0) return result;
        var salient = args.TryGetProperty("command", out var cmd) && cmd.GetString() is { } c ? c : args.TryGetProperty("file_path", out var fp) && fp.GetString() is { } f ? f : argsJson;
        var shortHay = string.Concat(toolName, " ", salient.Length <= 2000 ? salient : salient[..2000]);
        var fullHay = string.Concat(toolName, " ", TruncateArgs(argsJson));
        foreach (var pattern in _options.ForceAskPatterns) if (GlobMatch(pattern, shortHay) || GlobMatch(pattern, fullHay)) return new DecisionGateResult("confirm", Math.Max(result.Confidence, 0.8), ["force-ask"], result.Signals);
        return result;
    }

    private static string TruncateArgs(string argsJson) => argsJson.Length <= 2000 ? argsJson : argsJson[..2000];

    private static bool GlobMatch(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length) { if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t]))) { p++; t++; } else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; } else if (star >= 0) { p = star + 1; t = ++mark; } else return false; }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private GateSignals SignalsFor(string toolName, JsonElement? args, int blastRadius)
    {
        if (args is not { } parsed)
            return new GateSignals(false, false, true, blastRadius);
        var destructive = DecisionFastPaths.IsDestructive(toolName, parsed);
        var outwardFacing = IsEgress(toolName, parsed);
        var inScope = IsInScope(toolName, parsed);
        return new GateSignals(destructive, outwardFacing, inScope, blastRadius);
    }

    private bool IsEgress(string toolName, JsonElement args)
    {
        if (!string.Equals(toolName, "Bash", StringComparison.OrdinalIgnoreCase))
            return false;
        return args.TryGetProperty("command", out var cmd) &&
            cmd.GetString() is { } command &&
            DecisionFastPaths.LooksLikeEgress(command);
    }

    private bool IsInScope(string toolName, JsonElement args)
    {
        if (!args.TryGetProperty("file_path", out var path) || path.GetString() is not { } filePath)
            return true;
        if (string.IsNullOrWhiteSpace(filePath))
            return false;
        return !DecisionFastPaths.IsOutsideWorkingDirectory(filePath, _workingDirectory);
    }

    private static bool IsWipe(string toolName, JsonElement args)
    {
        if (!string.Equals(toolName, "Bash", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!args.TryGetProperty("command", out var cmd) || cmd.GetString() is not { } command)
            return false;
        var lowered = command.ToLowerInvariant();
        return WipeMarkers.Any(m => lowered.Contains(m, StringComparison.Ordinal));
    }

    private static bool ExfiltratesSecrets(string toolName, JsonElement args)
    {
        if (!args.TryGetProperty("command", out var cmd) || cmd.GetString() is not { } command)
        {
            var raw = args.GetRawText();
            return SecretScanner.Scan(raw).Count > 0 && DecisionFastPaths.LooksLikeEgress(raw);
        }
        return SecretScanner.Scan(command).Count > 0 && DecisionFastPaths.LooksLikeEgress(command);
    }
}
