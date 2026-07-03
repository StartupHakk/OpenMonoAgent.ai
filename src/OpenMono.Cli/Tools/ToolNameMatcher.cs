namespace OpenMono.Tools;

public static class ToolNameMatcher
{
    /// <summary>
    /// Checks if a tool name is allowed by an allow-list using glob patterns.
    /// Supports "*" (all), "prefix*" (prefix match), and exact matches (case-insensitive).
    /// </summary>
    public static bool IsAllowed(string toolName, IReadOnlyList<string> allowedTools)
    {
        foreach (var entry in allowedTools)
        {
            if (entry == "*")
                return true;

            if (entry.EndsWith('*'))
            {
                var prefix = entry[..^1];
                if (toolName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (toolName.Equals(entry, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
