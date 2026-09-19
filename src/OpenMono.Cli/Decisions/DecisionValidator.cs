namespace OpenMono.Decisions;

public static class DecisionValidator
{
    public static (IReadOnlyList<DecisionItem> Valid, IReadOnlyList<string> Errors) Validate(
        IReadOnlyList<DecisionItem> items,
        IReadOnlySet<string> vocabulary,
        int maxIndex)
    {
        var valid = new List<DecisionItem>(items.Count);
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var error = Check(item, vocabulary, maxIndex, seen);
            if (error is null)
            {
                seen.Add(item.CanonicalRef);
                valid.Add(item);
            }
            else
            {
                errors.Add(error);
            }
        }
        return (valid, errors);
    }

    private static string? Check(
        DecisionItem item,
        IReadOnlySet<string> vocabulary,
        int maxIndex,
        HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(item.Kind) || string.IsNullOrWhiteSpace(item.Id))
            return "empty-identity";
        if (seen.Contains(item.CanonicalRef))
            return $"duplicate:{item.CanonicalRef}";
        if (!vocabulary.Contains(item.Action))
            return $"unknown-action:{item.Action}";
        if (double.IsNaN(item.Probability) || double.IsInfinity(item.Probability))
            return $"non-finite-prob:{item.Id}";
        if (item.Probability < 0 || item.Probability > 1)
            return $"prob-out-of-range:{item.Id}";
        if (item.Index < 0 || item.Index > maxIndex)
            return $"index-out-of-bounds:{item.Id}";
        return null;
    }
}
