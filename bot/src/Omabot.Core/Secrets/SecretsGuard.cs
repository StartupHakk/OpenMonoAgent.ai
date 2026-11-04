// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Omabot.Secrets;

/// <summary>
/// Refuses empty, placeholder and example secrets (plan 7.12 rule 8). The
/// reason never contains any part of the value.
/// </summary>
public static class SecretsGuard
{
    private static readonly string[] Placeholders =
    [
        "changeme", "change-me", "change_me", "placeholder", "example", "your-key", "your_key", "yourkey",
        "your-token", "your_token", "insert", "replace-me", "replace_me", "todo", "dummy", "sample", "secret",
        "password", "letmein", "test", "xxxx", "<", ">", "...",
    ];

    public static string? Problem(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "is empty";
        var v = value.Trim();
        if (v.Length < 12) return "is shorter than 12 characters";
        var lower = v.ToLowerInvariant();
        if (lower.Distinct().Count() <= 2) return "repeats one or two characters";
        if (!lower.Contains("private key", StringComparison.Ordinal))
        {
            foreach (var p in Placeholders)
            {
                if (lower == p || (p.Length >= 4 && lower.Contains(p, StringComparison.Ordinal) && v.Length < 40))
                    return "looks like a placeholder or example value";
            }
        }
        return null;
    }
}
