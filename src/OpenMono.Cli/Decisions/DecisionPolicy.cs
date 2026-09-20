namespace OpenMono.Decisions;

public static class DecisionPolicy
{
    // Verify coverage cliffs, centralized in Phase 2 with values carried
    // over verbatim from HeuristicBackend. These cutoffs (0.6 supported,
    // 0.4 + negation contradicted) are unjustified measurements of nothing
    // yet — tuning waits for the Phase 3 Brier/ECE metric. Do not hand-tune.
    public const double VerifySupportedCoverage = 0.6;
    public const double VerifyContradictionCoverage = 0.4;
    public static string ApplyGate(string choice, double confidence, double auto, double review)
        => DecisionGate.Gate(confidence, auto, review) switch
        {
            GateVerdict.Act => choice,
            GateVerdict.Review => "review",
            _ => "escalate",
        };

    public static string NoulGate(double p, double auto = 0.85, double review = 0.6)
        => DecisionGate.GateNoul(p, auto, review) switch
        {
            GateVerdict.Act => p >= auto ? "yes" : "no",
            GateVerdict.Review => "review",
            _ => "escalate",
        };

    public static string ScoreBand(double score, double auto, double review)
        => DecisionGate.Gate(score, auto, review) switch
        {
            GateVerdict.Act => "auto",
            GateVerdict.Review => "review",
            _ => "escalate",
        };
}
