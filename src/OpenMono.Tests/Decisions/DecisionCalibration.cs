using System.Reflection;
using System.Text;
using System.Text.Json;
using OpenMono.Decisions;

namespace OpenMono.Tests.Decisions;

/// <summary>
/// Phase 3 calibration metric (Kinza section 9.3). Evaluates an
/// <see cref="IDecisionBackend"/> over decision-corpus.json into (predicted
/// probability, binary outcome) pairs and reports Brier score, binned ECE,
/// and a reliability table. The heuristic backend is the miscalibrated
/// control: its numbers are the baseline to beat, never a "calibrated"
/// claim. Nothing here tunes thresholds; tuning methodology arrives with
/// the gate work that consumes this metric.
/// </summary>
public static class DecisionCalibration
{
    public const int CorpusVersion = 1;
    public const int EceBins = 10;

    public sealed record Corpus(IReadOnlyList<CorpusEntry> Entries);

    public sealed record CorpusEntry(
        string Id,
        string Kind,
        string State,
        IReadOnlyDictionary<string, string> Options,
        string? Correct,
        string? Proposition,
        bool? Truth,
        string? Evidence,
        string? Claim,
        bool? Supported);

    public sealed record ReliabilityBin(
        int Index,
        double Low,
        double High,
        int Count,
        double MeanPredicted,
        double Accuracy);

    public sealed record CalibrationResult(
        int Total,
        int ChoiceCount,
        int NoulCount,
        int VerifyCount,
        double Brier,
        double Ece,
        IReadOnlyList<ReliabilityBin> Bins);

    public static Corpus LoadCorpus()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("decision-corpus.json", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("decision-corpus.json is not embedded.");
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("Cannot open decision-corpus.json.");
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        if (!root.TryGetProperty("version", out var ver) || ver.GetInt32() != CorpusVersion)
            throw new InvalidOperationException($"Corpus version must be {CorpusVersion}.");
        var entries = new List<CorpusEntry>();
        foreach (var el in root.GetProperty("entries").EnumerateArray())
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            if (el.TryGetProperty("options", out var opts))
                foreach (var o in opts.EnumerateObject())
                    options[o.Name] = o.Value.GetString() ?? string.Empty;
            entries.Add(new CorpusEntry(
                el.GetProperty("id").GetString() ?? throw new InvalidOperationException("Entry without id."),
                el.GetProperty("kind").GetString() ?? throw new InvalidOperationException("Entry without kind."),
                el.TryGetProperty("state", out var s) ? s.GetString() ?? string.Empty : string.Empty,
                options,
                el.TryGetProperty("correct", out var c) ? c.GetString() : null,
                el.TryGetProperty("proposition", out var p) ? p.GetString() : null,
                el.TryGetProperty("truth", out var t) ? t.GetBoolean() : null,
                el.TryGetProperty("evidence", out var e) ? e.GetString() : null,
                el.TryGetProperty("claim", out var cl) ? cl.GetString() : null,
                el.TryGetProperty("supported", out var sup) ? sup.GetBoolean() : null));
        }
        return new Corpus(entries);
    }

    /// <summary>
    /// Maps each corpus triple to (predicted P(event), outcome 0/1) under
    /// the given backend. Choice uses P(correct option); noul uses
    /// P(proposition); verify maps the verdict to P(supported).
    /// </summary>
    public static IReadOnlyList<(double P, int O)> Evaluate(IDecisionBackend backend, Corpus corpus)
    {
        var pairs = new List<(double P, int O)>();
        foreach (var e in corpus.Entries)
        {
            switch (e.Kind)
            {
                case "choice":
                    if (e.Correct is null) throw new InvalidOperationException($"{e.Id}: choice entry without 'correct'.");
                    var (_, _, probs) = backend.Choose(e.State, e.Options.ToDictionary(kv => kv.Key, kv => (string?)kv.Value));
                    if (!probs.TryGetValue(e.Correct, out var pc))
                        throw new InvalidOperationException($"{e.Id}: backend dropped the correct option.");
                    pairs.Add((Clamp01(pc), 1));
                    break;
                case "noul":
                    if (e.Proposition is null || e.Truth is null) throw new InvalidOperationException($"{e.Id}: noul entry needs proposition+truth.");
                    pairs.Add((Clamp01(backend.JudgeTrue(e.State, e.Proposition)), e.Truth.Value ? 1 : 0));
                    break;
                case "verify":
                    if (e.Evidence is null || e.Claim is null || e.Supported is null) throw new InvalidOperationException($"{e.Id}: verify entry needs evidence+claim+supported.");
                    var (verdict, conf) = backend.Verify(e.Evidence, e.Claim);
                    var pv = verdict switch
                    {
                        "supported" => conf,
                        "contradicted" => 1 - conf,
                        _ => 0.5,
                    };
                    pairs.Add((Clamp01(pv), e.Supported.Value ? 1 : 0));
                    break;
                default:
                    throw new InvalidOperationException($"{e.Id}: unknown kind '{e.Kind}'.");
            }
        }
        return pairs;
    }

    public static double BrierScore(IReadOnlyList<(double P, int O)> pairs)
    {
        if (pairs.Count == 0) throw new ArgumentException("No pairs.", nameof(pairs));
        return pairs.Average(t => (t.P - t.O) * (t.P - t.O));
    }

    public static (double Ece, IReadOnlyList<ReliabilityBin> Bins) ExpectedCalibrationError(
        IReadOnlyList<(double P, int O)> pairs, int bins = EceBins)
    {
        if (pairs.Count == 0) throw new ArgumentException("No pairs.", nameof(pairs));
        var result = new List<ReliabilityBin>();
        var ece = 0.0;
        for (var b = 0; b < bins; b++)
        {
            var low = (double)b / bins;
            var high = (double)(b + 1) / bins;
            var inBin = pairs.Where(t => t.P >= low && (b == bins - 1 ? t.P <= high : t.P < high)).ToList();
            if (inBin.Count == 0)
            {
                result.Add(new ReliabilityBin(b, low, high, 0, double.NaN, double.NaN));
                continue;
            }
            var mean = inBin.Average(t => t.P);
            var acc = inBin.Average(t => (double)t.O);
            ece += Math.Abs(acc - mean) * inBin.Count / pairs.Count;
            result.Add(new ReliabilityBin(b, low, high, inBin.Count, mean, acc));
        }
        return (ece, result);
    }

    public static CalibrationResult Calibrate(IDecisionBackend backend, Corpus corpus)
    {
        var pairs = Evaluate(backend, corpus);
        var (ece, bins) = ExpectedCalibrationError(pairs);
        return new CalibrationResult(
            corpus.Entries.Count,
            corpus.Entries.Count(e => e.Kind == "choice"),
            corpus.Entries.Count(e => e.Kind == "noul"),
            corpus.Entries.Count(e => e.Kind == "verify"),
            BrierScore(pairs),
            ece,
            bins);
    }

    public static string RenderMarkdown(CalibrationResult r, string backendLabel)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Decision Calibration (Phase 3 metric)");
        sb.AppendLine();
        sb.AppendLine("> Generated by `scripts/decision-calibrate.sh` from `decision-corpus.json` v1.");
        sb.AppendLine("> The heuristic backend is the **miscalibrated control** — these numbers are the");
        sb.AppendLine("> baseline to beat, not a calibration claim. Do not hand-tune constants to this corpus.");
        sb.AppendLine();
        sb.AppendLine($"- Backend: `{backendLabel}`");
        sb.AppendLine($"- Corpus: {r.Total} triples (choice {r.ChoiceCount}, noul {r.NoulCount}, verify {r.VerifyCount})");
        sb.AppendLine($"- Brier score: {r.Brier:F4} (lower is better; 0.25 ~= always predicting 0.5)");
        sb.AppendLine($"- ECE ({EceBins} bins): {r.Ece:F4} (lower is better; 0 = perfectly calibrated)");
        sb.AppendLine();
        sb.AppendLine("## Reliability diagram (binned)");
        sb.AppendLine();
        sb.AppendLine("| Bin | Predicted range | n | Mean predicted | Accuracy | Gap |");
        sb.AppendLine("| --- | --------------- | - | -------------- | -------- | --- |");
        foreach (var b in r.Bins)
        {
            var n = b.Count == 0 ? "0" : b.Count.ToString();
            var mean = b.Count == 0 ? "-" : b.MeanPredicted.ToString("F3");
            var acc = b.Count == 0 ? "-" : b.Accuracy.ToString("F3");
            var gap = b.Count == 0 ? "-" : Math.Abs(b.Accuracy - b.MeanPredicted).ToString("F3");
            sb.AppendLine($"| {b.Index} | [{b.Low:F1}, {b.High:F1}{(b.Index == EceBins - 1 ? "]" : ")")} | {n} | {mean} | {acc} | {gap} |");
        }
        return sb.ToString();
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);
}
