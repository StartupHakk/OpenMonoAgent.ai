using System.Security.Cryptography;

namespace OpenMono.Windows.Models;

/// <summary>
/// SHA256 verification for downloaded models and binaries.
/// Empty expected hashes are treated as unpinned (warn, do not fail) so the
/// registry stays usable before release pins land.
/// </summary>
public static class ChecksumVerifier
{
    public sealed record VerifyResult(bool Ok, bool Skipped, string Message);

    public static async Task<VerifyResult> VerifyFileAsync(
        string path,
        string expectedSha256,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return new VerifyResult(true, true, "No pinned checksum for this file, verification skipped.");
        }

        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        var expected = expectedSha256.Trim().ToLowerInvariant();
        if (actual == expected)
        {
            return new VerifyResult(true, false, "Checksum verified.");
        }

        return new VerifyResult(false, false, $"Checksum mismatch for {path}. Expected {expected}, got {actual}.");
    }

    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }
}
