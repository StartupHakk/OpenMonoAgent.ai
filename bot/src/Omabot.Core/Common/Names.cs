// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;

namespace Omabot.Common;

/// <summary>Name rules shared by the team file, paths and containers.</summary>
public static partial class Names
{
    public const int MaxBotName = 60;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,59}$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_.:-]{0,127}$")]
    private static partial Regex ToolNamePattern();

    /// <summary>Folder and container name for a bot: lower case, letters, digits and dashes.</summary>
    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        if (s.Length > MaxBotName) s = s[..MaxBotName].Trim('-');
        return s;
    }

    public static bool IsSafeSlug(string value) => SlugPattern().IsMatch(value);

    public static void RequireSafeSlug(string value, string what)
    {
        if (!IsSafeSlug(value))
            throw new ArgumentException($"Invalid {what} name for a path: only a to z, 0 to 9 and dashes are allowed.");
    }

    public static bool IsToolName(string value) => ToolNamePattern().IsMatch(value);

    public static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
