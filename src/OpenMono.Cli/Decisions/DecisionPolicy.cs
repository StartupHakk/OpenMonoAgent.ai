namespace OpenMono.Decisions;

public static class DecisionPolicy
{
    public static string ApplyGate(string choice, double confidence, double auto, double review)
        => confidence >= auto ? choice : confidence >= review ? "review" : "escalate";

    public static string NoulGate(double p, double auto = 0.85, double review = 0.6)
        => p >= auto ? "yes"
            : p <= (1 - auto) ? "no"
            : p >= review || p <= (1 - review) ? "review" : "escalate";

    public static string ScoreBand(double score, double auto, double review)
        => score >= auto ? "auto" : score >= review ? "review" : "escalate";
}
