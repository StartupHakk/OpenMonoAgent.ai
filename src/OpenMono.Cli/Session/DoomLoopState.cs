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
/// still escalates. The streak decays after <see cref="CleanBatchesToClear"/> consecutive
/// non-looping batches so a stale streak from much earlier work can't cause an instant
/// escalation on the next isolated repeat. Full clears happen on <see cref="Reset"/>
/// (a fresh user turn / playbook step / playbook run).
/// </summary>
public sealed class DoomLoopState
{
    /// <summary>
    /// Hardcoded decay threshold: this many consecutive clean (non-looping) batches clears
    /// the hit streak. Matches the period-1 detection window (3 identical batches), so the
    /// model must show a full window of varied work to earn a fresh slate. Kept hardcoded
    /// (not configurable) to avoid a tuning deadlock where a misconfigured decay either
    /// never clears or clears so fast the guard never escalates.
    /// </summary>
    public const int CleanBatchesToClear = 3;

    private int _consecutiveHits;
    private int _consecutiveClean;

    /// <summary>Consecutive doom-loop detections for the current task.</summary>
    public int ConsecutiveHits => Interlocked.CompareExchange(ref _consecutiveHits, 0, 0);

    /// <summary>Consecutive non-looping batches since the last hit.</summary>
    public int ConsecutiveClean => Interlocked.CompareExchange(ref _consecutiveClean, 0, 0);

    /// <summary>Current tier derived from the consecutive hit count.</summary>
    public DoomLoopTier Tier => DoomLoopTierExtensions.ForHits(ConsecutiveHits);

    /// <summary>Records a doom-loop detection. Returns the tier that just became active.</summary>
    public DoomLoopTier RecordHit()
    {
        Interlocked.Exchange(ref _consecutiveClean, 0);
        var hits = Interlocked.Increment(ref _consecutiveHits);
        return DoomLoopTierExtensions.ForHits(hits);
    }

    /// <summary>
    /// Records a non-looping batch. After <see cref="CleanBatchesToClear"/> in a row the
    /// hit streak is cleared. Returns true when this call cleared a non-zero streak.
    /// </summary>
    public bool RecordClean()
    {
        var clean = Interlocked.Increment(ref _consecutiveClean);
        if (clean < CleanBatchesToClear)
            return false;
        var hadStreak = Interlocked.Exchange(ref _consecutiveHits, 0) != 0;
        if (hadStreak)
            Interlocked.Exchange(ref _consecutiveClean, 0);
        return hadStreak;
    }

    /// <summary>Clears the streak. Call at the start of each new user turn / playbook step.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _consecutiveHits, 0);
        Interlocked.Exchange(ref _consecutiveClean, 0);
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

/// <summary>
/// Canonical model-facing doom-loop prompts, shared by the interactive turn loop
/// (ConversationLoop) and the playbook dispatch path (ToolDispatcher) so both escalate
/// with identical instructions.
/// </summary>
public static class DoomLoopPrompts
{
    private const int MaxPatternChars = 600;

    // Arms are in escalation order (standard → strong); the fallback matches Nudge
    // so any unexpected tier degrades to the gentlest instruction, never the sternest.
    public static string Nudge(string names, DoomLoopTier tier) =>
        tier == DoomLoopTier.StrongNudge ? StrongNudgeText(names) : StandardNudgeText(names);

    private static string StandardNudgeText(string names) =>
        $"[System: Doom loop (1st) — you called {names} again with identical arguments. The previous attempt did not make progress. Do NOT repeat the exact same call: briefly explain what you are trying to accomplish and what the previous output told you, then take a structurally different step (change an argument, use a different tool, or gather more information first).]";

    private static string StrongNudgeText(string names) =>
        $"[System: Doom loop (escalated) — {names} has been repeated with identical arguments. Repeating it will not help. You MUST stop calling {names}. First, briefly explain to yourself what you are trying to accomplish, what you already tried, and what you learned from the previous outputs — then take a structurally different step (fix the underlying problem first, change an argument, or use a different tool). If you keep repeating, the turn will be ended and escalated.]";

    /// <summary>
    /// Nudge with the concrete repeating pattern appended so the model sees exactly which
    /// calls triggered the guard (e.g. "Bash(graph.py) → Bash(grep) → Bash(graph.py)").
    /// Pattern text is truncated to keep the prompt bounded.
    /// </summary>
    public static string NudgeWithPattern(string names, DoomLoopTier tier, string pattern, int hits)
    {
        var body = Nudge(names, tier);
        if (string.IsNullOrWhiteSpace(pattern))
            return $"{body} (detection {hits}/5)";
        var short_pattern = pattern.Length > MaxPatternChars
            ? "..." + pattern[^MaxPatternChars..]
            : pattern;
        return $"{body} (detection {hits}/5)\nRepeating pattern:\n{short_pattern}";
    }

    public static string Max(string names) =>
        $"[System: Doom loop (max) — {names} has been repeated too many times with identical arguments. This turn is being ended and escalated to the user. Stop and explain to the user what you were trying to do, what you tried, and what you need from them. Do not attempt further tool calls.]";

    /// <summary>Terminal-tier message with the concrete pattern, so a re-run has context.</summary>
    public static string MaxWithPattern(string names, string pattern, int hits)
    {
        var body = Max(names);
        if (string.IsNullOrWhiteSpace(pattern))
            return $"{body} (detection {hits}/5)";
        var short_pattern = pattern.Length > MaxPatternChars
            ? "..." + pattern[^MaxPatternChars..]
            : pattern;
        return $"{body} (detection {hits}/5)\nRepeating pattern:\n{short_pattern}";
    }

    public static string NudgeLabel(DoomLoopTier tier) =>
        tier == DoomLoopTier.Nudge ? "nudging" : "escalating the nudge";
}
