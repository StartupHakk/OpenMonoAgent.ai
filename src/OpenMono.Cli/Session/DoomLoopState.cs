namespace OpenMono.Session;

public enum DoomLoopTier
{
    None = 0,
    Nudge = 1,
    StrongNudge = 2,
    Escalate = 3,
}

/// <summary>
/// Tracks how many consecutive times the doom-loop detector has fired, so the caller can
/// escalate in tiers instead of hard-stopping on the first detection. A hit is counted per
/// detector firing — the detector itself is what decides whether a batch of tool calls forms
/// a repeating pattern (period 1-4), so a looping model that cycles between two signatures
/// still escalates. The streak only clears on <see cref="Reset"/> (a fresh user turn / playbook
/// step), or implicitly when the model returns to non-looping work (the detector simply stops
/// firing, so the count stops growing).
/// </summary>
public sealed class DoomLoopState
{
    private int _consecutiveHits;

    /// <summary>Consecutive doom-loop detections for the current task.</summary>
    public int ConsecutiveHits => Interlocked.CompareExchange(ref _consecutiveHits, 0, 0);

    /// <summary>Current tier derived from the consecutive hit count.</summary>
    public DoomLoopTier Tier => DoomLoopTierExtensions.ForHits(ConsecutiveHits);

    /// <summary>Records a doom-loop detection. Returns the tier that just became active.</summary>
    public DoomLoopTier RecordHit()
    {
        var hits = Interlocked.Increment(ref _consecutiveHits);
        return DoomLoopTierExtensions.ForHits(hits);
    }

    /// <summary>Clears the streak. Call at the start of each new user turn / playbook step.</summary>
    public void Reset() => Interlocked.Exchange(ref _consecutiveHits, 0);
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

/// <summary>
/// Canonical model-facing doom-loop prompts, shared by the interactive turn loop
/// (ConversationLoop) and the playbook dispatch path (ToolDispatcher) so both escalate
/// with identical instructions.
/// </summary>
public static class DoomLoopPrompts
{
    public static string Nudge(string names, DoomLoopTier tier) => tier switch
    {
        DoomLoopTier.StrongNudge =>
            $"[System: Doom loop (escalated) — {names} has been repeated with identical arguments. Repeating it will not help. You MUST stop calling {names}. Either fix the underlying problem first, use a different tool/arguments, or stop and explain to the user what is blocking you and what you need. If you keep repeating, the turn will be ended and escalated.]",
        _ =>
            $"[System: Doom loop (1st) — you called {names} again with identical arguments. The previous attempt did not make progress. Do NOT repeat the exact same call: inspect the earlier output and take a structurally different step (change an argument, use a different tool, or gather more information first).]",
    };

    public static string Max(string names) =>
        $"[System: Doom loop (max) — {names} has been repeated too many times with identical arguments. This turn is being ended and escalated to the user. Do not attempt further tool calls.]";

    public static string NudgeLabel(DoomLoopTier tier) =>
        tier == DoomLoopTier.Nudge ? "nudging" : "escalating the nudge";
}
