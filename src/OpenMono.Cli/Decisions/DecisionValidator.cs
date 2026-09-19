namespace OpenMono.Decisions;

public static class DecisionValidator
{
    public static (IReadOnlyList<DecisionItem> Valid, IReadOnlyList<string> Errors) Validate(
        IReadOnlyList<DecisionItem> items,
        IReadOnlySet<string> vocabulary,
        int maxIndex)
    {
        if (items.Count == 0)
            return ([], ["menu-empty"]);
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
        if (items.Count > 255)
            errors.Add($"menu-overflow:{items.Count}");
        return (valid, errors);
    }

    public static IReadOnlyList<string> ValidateChoice(
        string instructions,
        IReadOnlyList<(string Key, string? Description)> options,
        string choice,
        IReadOnlyDictionary<string, double> probabilities)
    {
        var errors = new List<string>();
        var textError = ClosedWorldChecks.CheckNonEmpty(instructions);
        if (textError is not null)
            errors.Add(textError);
        var countError = ClosedWorldChecks.CheckOptionCount(options.Count);
        if (countError is not null)
            errors.Add(countError);
        var sumError = ClosedWorldChecks.CheckProbabilitiesSum(probabilities);
        if (sumError is not null)
            errors.Add(sumError);
        else
        {
            var argmaxError = ClosedWorldChecks.CheckChoiceIsArgmax(choice, probabilities);
            if (argmaxError is not null)
                errors.Add(argmaxError);
        }
        return errors;
    }

    public static IReadOnlyList<string> ValidateLevels(IReadOnlyList<string> levels)
    {
        var errors = new List<string>();
        var countError = ClosedWorldChecks.CheckLevelCount(levels.Count);
        if (countError is not null)
            errors.Add(countError);
        foreach (var level in levels)
        {
            var levelError = ClosedWorldChecks.CheckNonEmpty(level);
            if (levelError is not null)
                errors.Add(levelError);
        }
        return errors;
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
        if (item.Detail is not null && string.IsNullOrWhiteSpace(item.Detail))
            return $"empty-value:{item.Id}";
        return null;
    }
}
