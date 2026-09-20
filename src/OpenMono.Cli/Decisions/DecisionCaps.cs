namespace OpenMono.Decisions;

/// <summary>
/// Single home for decision-layer size caps (Phase 2). Previously
/// DecideEvaluateTool (8000) and ChiefRouter (200000) each carried their
/// own magic number; both now forward to these constants. Overflow
/// semantics are unchanged per call site but always surfaced: evaluate
/// reports <c>truncated</c>, routing reports <c>Capped</c> — never silent.
/// </summary>
public static class DecisionCaps
{
    /// <summary>Shared-state ceiling for batch evaluate/rank/verify paths.</summary>
    public const int MaxStateChars = 8000;

    /// <summary>State ceiling for chief routing (goal + completed work).</summary>
    public const int MaxRouteStateChars = 200000;

    /// <summary>Raw tool-input ceiling for the evaluate path.</summary>
    public const int MaxInputChars = 65536;
}
