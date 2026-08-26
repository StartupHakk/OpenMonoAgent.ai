namespace OpenMono.Session;

public enum DoomLoopTier
{
    None = 0,
    Nudge = 1,
    StrongNudge = 2,
    Escalate = 3,
}

/// <summary>
/// Tracks how many consecutive times the doom-loop detector has fired for the current
/// batch of tool calls, so the caller can escalate in tiers instead of hard-stopping on
/// the first detection. The counter is per-session: it resets when the tool-call
/// signature changes, when a call succeeds with fresh output, or when a new user turn /
/// playbook step begins.
/// </summary>
public sealed class DoomLoopState
{
    private int _consecutiveHits;
    private string? _lastSignature;

    /// <summary>Consecutive doom-loop detections for the current (unchanged) call signature.</summary>
    public int ConsecutiveHits => Volatile.Read(ref _consecutiveHits);

    /// <summary>Current tier derived from the consecutive hit count.</summary>
    public DoomLoopTier Tier => DoomLoopTierExtensions.ForHits(ConsecutiveHits);

    /// <summary>
    /// Records a doom-loop detection for the given signature. If the signature differs
    /// from the previous one the counter restarts at 1 (the model tried something new, so
    /// the previous streak no longer applies). Returns the tier that just became active.
    /// </summary>
    public DoomLoopTier RecordHit(string signature)
    {
        if (!string.Equals(signature, _lastSignature, StringComparison.Ordinal))
        {
            _consecutiveHits = 0;
            _lastSignature = signature;
        }

        _consecutiveHits++;
        return DoomLoopTierExtensions.ForHits(_consecutiveHits);
    }

    /// <summary>Clears the streak. Call at the start of each new user turn / playbook step.</summary>
    public void Reset()
    {
        _consecutiveHits = 0;
        _lastSignature = null;
    }
}

public static class DoomLoopTierExtensions
{
    public static DoomLoopTier ForHits(int hits) => hits switch
    {
        >= 5 => DoomLoopTier.Escalate,
        >= 3 => DoomLoopTier.StrongNudge,
        >= 1 => DoomLoopTier.Nudge,
        _ => DoomLoopTier.None,
    };
}
