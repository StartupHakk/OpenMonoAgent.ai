// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Omabot.Web;

/// <summary>
/// Web account passwords (plan 7.20 item 2): PBKDF2 with SHA-256, a random
/// salt per password and the iteration count stored with the hash, compared
/// in fixed time. The strength rule follows length over composition.
/// </summary>
public static class Passwords
{
    public const int DefaultIterations = 600_000;
    public const int MinIterations = 1_000;
    public const int MinLength = 12;
    public const int MaxLength = 128;

    private static readonly Lazy<string> Dummy = new(() => Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))));

    /// <summary>A hash to check against when the account does not exist, so a missing name costs the same time as a wrong password.</summary>
    public static string DummyHash => Dummy.Value;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        if (iterations < MinIterations) throw new ArgumentOutOfRangeException(nameof(iterations));
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${iterations.ToString(CultureInfo.InvariantCulture)}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations < MinIterations) return false;
        byte[] salt, expected;
        try { salt = Convert.FromBase64String(parts[2]); expected = Convert.FromBase64String(parts[3]); }
        catch (FormatException) { return false; }
        if (password.Length > MaxLength) return false;
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(key, expected);
    }

    private static readonly string[] Common =
    [
        "password", "passw0rd", "letmein", "welcome", "qwerty", "qwertyuiop", "asdfghjkl", "zxcvbnm", "iloveyou", "admin", "administrator",
        "changeme", "trustno1", "football", "baseball", "monkey", "dragon", "master", "sunshine", "princess", "shadow", "superman", "batman",
        "starwars", "whatever", "openmono", "omabot", "roster", "secret", "abc123", "123456", "1234567890", "0987654321", "111111", "000000",
    ];

    /// <summary>Why a new password is refused, or null. At least 12 characters, not built on the account name or a common word, not one repeated pattern.</summary>
    public static string? Problem(string password, string account)
    {
        if (password.Length < MinLength) return $"Use at least {MinLength} characters.";
        if (password.Length > MaxLength) return $"Use at most {MaxLength} characters.";
        if (password.Any(char.IsControl)) return "Control characters are not allowed.";
        if (password.Distinct().Count() < 6) return "Use more different characters.";
        var lower = password.ToLowerInvariant();
        if (account.Length >= 3 && lower.Contains(account.ToLowerInvariant(), StringComparison.Ordinal)) return "Do not build the password on the account name.";
        var letters = new string(lower.Where(char.IsLetterOrDigit).ToArray());
        foreach (var w in Common)
        {
            if (letters.Length - w.Length < 6 && letters.Contains(w, StringComparison.Ordinal)) return "That password is too common.";
        }
        for (var n = 1; n <= password.Length / 3; n++)
        {
            var unit = password[..n];
            if (password.Length % n == 0 && string.Concat(Enumerable.Repeat(unit, password.Length / n)) == password) return "Do not repeat one short pattern.";
        }
        return null;
    }
}
