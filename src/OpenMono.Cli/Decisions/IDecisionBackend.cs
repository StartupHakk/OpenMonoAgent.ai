namespace OpenMono.Decisions;

/// <summary>
/// Pluggable scoring backend seam (Phase 1). Every decision tool programs
/// against this interface; <see cref="HeuristicBackend"/> is the default
/// implementation and runtime behavior with it is byte-identical to before
/// the seam existed. A future SystemOne backend plugs in here without
/// touching any tool.
/// </summary>
public interface IDecisionBackend
{
    (string Choice, double Confidence, IReadOnlyDictionary<string, double> Probabilities) Choose(
        string state,
        IReadOnlyDictionary<string, string?> options,
        CancellationToken ct = default);

    double JudgeTrue(
        string state,
        string proposition,
        CancellationToken ct = default);

    double Relevance(
        string query,
        string candidate,
        CancellationToken ct = default);

    (string Verdict, double Confidence) Verify(
        string evidence,
        string claim,
        CancellationToken ct = default);
}
