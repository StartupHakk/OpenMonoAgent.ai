namespace OpenMono.Decisions;

public static class ChoiceMenu
{
    public const string OtherKey = "other";

    public static IReadOnlyDictionary<string, string?> Build(string id, IReadOnlyList<(string Key, string? Description)> items, bool includeOther = true)
    {
        var invalid = ClosedWorldChecks.CheckOptionCount(items.Count);
        if (invalid is not null)
            throw new InvalidOperationException(invalid);
        var menu = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var item in items)
            menu.TryAdd(item.Key, item.Description);
        if (includeOther)
            menu.TryAdd(OtherKey, "None of the listed options fits.");
        return menu;
    }

    public static IReadOnlyList<(string Key, string? Description)> Filter(IReadOnlyList<(string Key, string? Description)> items, string? includeCsv, string? excludeCsv)
    {
        var includes = SplitCsv(includeCsv);
        var excludes = SplitCsv(excludeCsv);
        var kept = new List<(string Key, string? Description)>(items.Count);
        foreach (var item in items)
        {
            if (includes.Count > 0 && !MatchesAny(item.Key, includes))
                continue;
            if (MatchesAny(item.Key, excludes))
                continue;
            kept.Add(item);
        }
        var overflow = kept.Count > 255 ? ClosedWorldChecks.CheckOptionCount(kept.Count) : null;
        if (overflow is not null)
            throw new InvalidOperationException(overflow);
        return kept;
    }

    private static List<string> SplitCsv(string? csv)
    {
        var patterns = new List<string>();
        if (string.IsNullOrEmpty(csv))
            return patterns;
        foreach (var part in csv.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
                patterns.Add(trimmed);
        }
        return patterns;
    }

    private static bool MatchesAny(string key, List<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (GlobMatch(pattern, key))
                return true;
        }
        return false;
    }

    private static bool GlobMatch(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length) { if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t]))) { p++; t++; } else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; } else if (star >= 0) { p = star + 1; t = ++mark; } else return false; }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
