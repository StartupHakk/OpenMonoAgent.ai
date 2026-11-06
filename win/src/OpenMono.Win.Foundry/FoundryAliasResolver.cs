namespace OpenMono.Win.Foundry;

// Resolves the desired model alias against the real Foundry catalog spelling.
// The alias in the plan (qwen3.8-27b) is a wish, not a hash: catalog entries
// vary by release, so match exact first, then punctuation-insensitive.
public static class FoundryAliasResolver
{
    public static string? Resolve(string desiredAlias, IEnumerable<string> catalog)
    {
        if (string.IsNullOrWhiteSpace(desiredAlias))
        {
            return null;
        }

        var entries = catalog.Where(e => !string.IsNullOrWhiteSpace(e)).ToList();

        var exact = entries.FirstOrDefault(e =>
            string.Equals(e, desiredAlias, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var normalizedDesired = Normalize(desiredAlias);
        return entries.FirstOrDefault(e =>
            string.Equals(Normalize(e), normalizedDesired, StringComparison.OrdinalIgnoreCase));
    }

    public static string Normalize(string alias)
    {
        var builder = new System.Text.StringBuilder(alias.Length);
        foreach (var ch in alias)
        {
            if (ch is '-' or '_' or ' ' or '.')
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }
}
