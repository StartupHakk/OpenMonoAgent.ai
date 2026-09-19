namespace OpenMono.Decisions;

public static class ClosedWorldChecks
{
    public static string? CheckOptionCount(int count) =>
        count < 1 ? "menu-empty" : count > 255 ? $"menu-overflow:{count}" : null;

    public static string? CheckLevelCount(int count) =>
        count < 2 || count > 10 ? $"levels-out-of-range:{count}" : null;

    public static string? CheckProbabilitiesSum(IReadOnlyDictionary<string, double> probs)
    {
        var sum = 0.0;
        foreach (var kv in probs)
        {
            if (double.IsNaN(kv.Value) || double.IsInfinity(kv.Value))
                return "non-finite-prob";
            if (kv.Value < 0 || kv.Value > 1)
                return "prob-out-of-range";
            sum += kv.Value;
        }
        return Math.Abs(sum - 1) > 1e-6 ? $"probs-not-normalized:{sum:F4}" : null;
    }

    public static string? CheckChoiceIsArgmax(string choice, IReadOnlyDictionary<string, double> probs)
    {
        string? bestKey = null;
        var best = double.NegativeInfinity;
        foreach (var kv in probs)
        {
            if (kv.Value > best)
            {
                best = kv.Value;
                bestKey = kv.Key;
            }
        }
        return bestKey is not null && string.Equals(choice, bestKey, StringComparison.Ordinal) ? null : $"choice-not-argmax:{choice}";
    }

    public static string? CheckNonEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? "empty-value" : null;
}
