namespace OpenMono.Decisions;

public static class DecisionOrderer
{
    private static readonly HashSet<string> TerminalActions =
        new(StringComparer.OrdinalIgnoreCase) { "done", "submit" };

    private static readonly HashSet<string> VerifyActions =
        new(StringComparer.OrdinalIgnoreCase) { "verify", "review", "check", "test" };

    public static (IReadOnlyList<DecisionItem> ExecutionOrder, IReadOnlyList<DecisionItem> Skipped) Order(
        IReadOnlyList<DecisionItem> decisions,
        double minConfidence,
        bool allowTerminal)
    {
        var kept = new List<DecisionItem>(decisions.Count);
        var skipped = new List<DecisionItem>();
        var terminalKept = false;
        foreach (var item in decisions)
        {
            if (string.Equals(item.Action, "skip", StringComparison.OrdinalIgnoreCase) ||
                item.Probability < minConfidence)
            {
                skipped.Add(item);
                continue;
            }
            if (TerminalActions.Contains(item.Action))
            {
                if (!allowTerminal || terminalKept)
                {
                    skipped.Add(item);
                    continue;
                }
                terminalKept = true;
            }
            kept.Add(item);
        }
        var ordered = kept.OrderBy(Tier).ToList();
        return (ordered, skipped);
    }

    private static int Tier(DecisionItem item)
    {
        if (TerminalActions.Contains(item.Action))
            return 2;
        if (VerifyActions.Contains(item.Action))
            return 1;
        return 0;
    }
}
