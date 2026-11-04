// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Omabot.Common;

/// <summary>
/// Secret redaction for memory, logs, audit lines and refusal messages
/// (plan 7.3 table and 7.12). Every match becomes «redacted N chars».
/// The exact values of known roster secrets are always replaced.
/// </summary>
public sealed partial class Redactor
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);

    public static Redactor Shared { get; } = new();

    public static string Mark(int length) => $"\u00abredacted {length} chars\u00bb";

    [GeneratedRegex(@"-----BEGIN [A-Z0-9 ]*PRIVATE KEY-----[\s\S]*?(-----END [A-Z0-9 ]*PRIVATE KEY-----|$)")]
    private static partial Regex PrivateKeyBlock();

    [GeneratedRegex(@"\b(?:sk-(?:ant-|proj-)?[A-Za-z0-9_\-]{16,}|xox[abprs]-[A-Za-z0-9\-]{10,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9_\-]{16,}|(?:AKIA|ASIA)[A-Z0-9]{16}|omr_[A-Za-z0-9_\-]{12,}|omb_[A-Za-z0-9_\-]{16,}|AIza[0-9A-Za-z_\-]{30,})")]
    private static partial Regex KnownPrefixes();

    [GeneratedRegex(@"(?i)(\bbearer\s+)([A-Za-z0-9\-._~+/]{8,}=*)")]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?i)(\b(?:password|passwd|pwd|secret|token|api[_-]?key|apikey|access[_-]?key|client[_-]?secret|private[_-]?key|auth)\b[""']?\s*[:=]\s*[""']?)([^\s""',;]{4,})")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"\b([a-z][a-z0-9+.\-]*://[^:/\s@]+:)([^@/\s]+)(@)")]
    private static partial Regex ConnectionString();

    [GeneratedRegex(@"(?i)(\b(?:key|token|secret|credential|signature|sig|hash|bearer|auth)[A-Za-z_\-]*\b[^A-Za-z0-9\n]{1,4})([A-Za-z0-9+/=_\-]{32,})")]
    private static partial Regex HighEntropyNearKey();

    /// <summary>Add an exact secret value that must never appear anywhere.</summary>
    public void AddKnownSecret(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 6) return;
        lock (_lock) _known.Add(value);
    }

    public void RemoveKnownSecret(string value)
    {
        lock (_lock) _known.Remove(value);
    }

    public string Redact(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;
        var s = input;
        string[] known;
        lock (_lock) known = [.. _known.OrderByDescending(k => k.Length)];
        foreach (var k in known)
        {
            if (s.Contains(k, StringComparison.Ordinal)) s = s.Replace(k, Mark(k.Length), StringComparison.Ordinal);
        }
        s = PrivateKeyBlock().Replace(s, m => Mark(m.Length));
        s = Bearer().Replace(s, m => m.Groups[1].Value + Mark(m.Groups[2].Length));
        s = ConnectionString().Replace(s, m => m.Groups[1].Value + Mark(m.Groups[2].Length) + m.Groups[3].Value);
        s = KnownPrefixes().Replace(s, m => Mark(m.Length));
        s = KeyValue().Replace(s, m => m.Groups[2].Value.StartsWith('\u00ab') ? m.Value : m.Groups[1].Value + Mark(m.Groups[2].Length));
        s = HighEntropyNearKey().Replace(s, m => m.Groups[1].Value + Mark(m.Groups[2].Length));
        return s;
    }

    /// <summary>
    /// True when the text holds a secret with an unmistakable shape (a known
    /// key prefix or a private key block). Used by doctor scans, where the
    /// looser key=value patterns would be too noisy to fail on.
    /// </summary>
    public static bool ContainsStrongSecret(string? input)
        => !string.IsNullOrEmpty(input) && (KnownPrefixes().IsMatch(input) || PrivateKeyBlock().IsMatch(input));

    /// <summary>True when redaction would change the text.</summary>
    public bool ContainsSecret(string? input) => !string.IsNullOrEmpty(input) && Redact(input) != input;

    /// <summary>
    /// Find the first line that holds a secret. Returns the 1-based line number,
    /// or 0 when clean. Never returns the value itself (lesson L11, UNIT-TEAM-10).
    /// </summary>
    public int FirstSecretLine(string text)
    {
        var lines = SecureFile.NormalizeLf(text).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (ContainsSecret(lines[i])) return i + 1;
        }
        return ContainsSecret(text) ? 1 : 0;
    }

    /// <summary>A short description of what kind of secret matched, never the value.</summary>
    public static string Describe(string text)
    {
        if (PrivateKeyBlock().IsMatch(text)) return "private key block";
        if (Bearer().IsMatch(text)) return "bearer token";
        if (ConnectionString().IsMatch(text)) return "connection string password";
        if (KnownPrefixes().IsMatch(text)) return "API key";
        if (KeyValue().IsMatch(text)) return "key and value pair";
        if (HighEntropyNearKey().IsMatch(text)) return "high entropy value";
        return "known roster secret";
    }
}
