// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Omabot.Common;
using Omabot.Config;

namespace Omabot.Web;

/// <summary>
/// The web admin secret (plan 6.5 and 7.20 item 1): required before web mode
/// starts, created on the host, never printed. Two-factor seeds are sealed
/// under a key derived from it, so users.db alone never yields a second factor.
/// </summary>
public static class WebSecrets
{
    public const int MinLength = 32;

    public static string PathOf(RosterConfig config, RosterPaths paths) => config.Web.AdminSecretFile ?? paths.WebAdminSecret;

    /// <summary>Why the admin secret cannot be used, or null.</summary>
    public static string? Problem(string path)
    {
        if (!File.Exists(path)) return $"The web admin secret {path} does not exist. Run: oma-bot web admin-secret";
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || SecureFile.HasSymlinkSegment(info.FullName)) return $"The web admin secret {path} is a symlink or sits under one.";
        if (!SecureFile.IsPrivate(path)) return $"The web admin secret {path} is readable by others. Run chmod 600 {path}.";
        var text = File.ReadAllText(path).Trim();
        if (text.Length < MinLength || text.Distinct().Count() < 16) return $"The web admin secret {path} is too weak. Replace it with: oma-bot web admin-secret --rotate";
        return null;
    }

    /// <summary>Creates a random admin secret when none exists. Returns false when one is already there.</summary>
    public static bool Create(string path)
    {
        if (File.Exists(path)) return false;
        SecureFile.EnsureDirNoClobber(Path.GetDirectoryName(path)!);
        SecureFile.WriteAllTextAtomic(path, NewSecret() + "\n");
        return true;
    }

    public static string NewSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The key two-factor seeds are sealed under. Throws when the secret is missing or unusable.</summary>
    public static byte[] SeedKey(string path)
    {
        if (Problem(path) is { } p) throw new InvalidOperationException(p);
        return SeedKeyFrom(File.ReadAllText(path).Trim());
    }

    public static byte[] SeedKeyFrom(string secret)
    {
        Redactor.Shared.AddKnownSecret(secret);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), 32, info: Encoding.ASCII.GetBytes("oma-bot web totp seed v1"));
    }
}
