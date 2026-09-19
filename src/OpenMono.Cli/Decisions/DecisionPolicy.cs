namespace OpenMono.Decisions;

public static class DecisionPolicy
{
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
