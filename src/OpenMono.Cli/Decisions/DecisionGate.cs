namespace OpenMono.Decisions;

public enum GateVerdict { Act, Review, Escalate }

public static class DecisionGate
{
    public static GateVerdict Gate(double confidence, double auto, double review) =>
        confidence >= auto ? GateVerdict.Act : confidence >= review ? GateVerdict.Review : GateVerdict.Escalate;

    public static GateVerdict GateNoul(double p, double auto, double review) =>
        p >= auto ? GateVerdict.Act : p <= (1 - auto) ? GateVerdict.Act : p >= review || p <= (1 - review) ? GateVerdict.Review : GateVerdict.Escalate;
}
