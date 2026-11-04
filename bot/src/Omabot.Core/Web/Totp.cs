// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Omabot.Web;

/// <summary>
/// Time-based one-time codes (RFC 6238 with RFC 4226): SHA-1, 30 second
/// steps, 6 digits, one step of drift either way. A code is accepted once:
/// the step it matched is stored and earlier or equal steps are refused.
/// Seeds are sealed with AES-GCM under a key derived from the web admin
/// secret, bound to the account name.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;

    public static byte[] NewSeed() => RandomNumberGenerator.GetBytes(20);

    public static long StepAt(DateTimeOffset t) => t.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(byte[] seed, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--) { counter[i] = (byte)(step & 0xff); step >>= 8; }
        var mac = HMACSHA1.HashData(seed, counter);
        var offset = mac[^1] & 0x0f;
        var value = ((mac[offset] & 0x7f) << 24) | (mac[offset + 1] << 16) | (mac[offset + 2] << 8) | mac[offset + 3];
        return (value % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>The step a fresh code matched, or null. Steps at or before <paramref name="lastStep"/> are refused (no replay).</summary>
    public static long? Verify(byte[] seed, string? code, DateTimeOffset now, long lastStep)
    {
        if (code is null) return null;
        code = code.Trim();
        if (code.Length != Digits || !code.All(char.IsAsciiDigit)) return null;
        var current = StepAt(now);
        long? matched = null;
        for (var s = current - 1; s <= current + 1; s++)
        {
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(seed, s)), Encoding.ASCII.GetBytes(code)) && s > lastStep) matched ??= s;
        }
        return matched;
    }

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Base32(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5) { sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] FromBase32(string text)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var ch in text.TrimEnd('=').ToUpperInvariant())
        {
            var v = Alphabet.IndexOf(ch, StringComparison.Ordinal);
            if (v < 0) throw new FormatException("Not base32.");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 0xff)); bits -= 8; }
        }
        return [.. bytes];
    }

    public static string ProvisioningUri(string issuer, string account, byte[] seed)
    {
        var label = Uri.EscapeDataString(issuer) + ":" + Uri.EscapeDataString(account);
        return $"otpauth://totp/{label}?secret={Base32(seed)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }

    public static string Seal(byte[] seed, byte[] key, string account)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[seed.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, seed, cipher, tag, Encoding.UTF8.GetBytes("account:" + account));
        return "v1." + Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    /// <summary>The seed, or null when the sealed value is damaged, belongs to another account or was sealed under another key.</summary>
    public static byte[]? Open(string sealedSeed, byte[] key, string account)
    {
        if (!sealedSeed.StartsWith("v1.", StringComparison.Ordinal)) return null;
        byte[] raw;
        try { raw = Convert.FromBase64String(sealedSeed[3..]); }
        catch (FormatException) { return null; }
        if (raw.Length < 12 + 16 + 10) return null;
        var nonce = raw[..12];
        var tag = raw[^16..];
        var cipher = raw[12..^16];
        var seed = new byte[cipher.Length];
        try
        {
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(nonce, cipher, tag, seed, Encoding.UTF8.GetBytes("account:" + account));
            return seed;
        }
        catch (CryptographicException) { return null; }
    }
}
