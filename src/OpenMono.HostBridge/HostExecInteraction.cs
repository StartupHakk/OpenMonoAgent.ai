using System.Text;
using System.Text.RegularExpressions;

namespace OpenMono.HostBridge;

/// <summary>How a command is expected to interact, for the pre-exec hint only. Never blocking.</summary>
public enum InteractiveHint
{
    None,
    /// <summary>Reads /dev/tty — stdin forwarding cannot work (see Phase 2: ASKPASS/PTY).</summary>
    TtySecret,
    StdinSecret,
    StdinConfirm,
    TtyRisk,
}

/// <summary>What a matched output tail looks like.</summary>
public enum PromptKind
{
    None,
    Secret,
    Confirm,
    Text,
}

/// <summary>
/// Operator prompt back-channel for commands that block on input.
/// Implemented by the bridge over the existing UI (masked / echoed / [y/N]).
/// Answers are per-command and never cached as session grants.
/// </summary>
public interface IHostExecPrompter
{
    /// <summary>False when prompting is impossible (non-interactive, redirected stdin).</summary>
    bool CanPrompt { get; }

    /// <summary>Masked prompt. Null = declined, timed out, or impossible. Never logged.</summary>
    Task<char[]?> AskSecretAsync(string promptExcerpt, CancellationToken ct);

    /// <summary>Echoed prompt. Null = declined, timed out, or impossible.</summary>
    Task<string?> AskTextAsync(string promptExcerpt, CancellationToken ct);

    /// <summary>[y/N] prompt. Null = declined, timed out, or impossible.</summary>
    Task<bool?> AskConfirmAsync(string promptExcerpt, CancellationToken ct);
}

/// <summary>
/// Static (hint-only) command classification + streaming prompt detection for
/// HOST_EXEC foreground runs. Runtime detection is the primary mechanism —
/// no static list survives an LLM that can run anything.
/// </summary>
internal static class HostExecInteraction
{
    internal const int MaxPromptsPerCommand = 3;
    internal static readonly TimeSpan PromptSettleDelay = TimeSpan.FromMilliseconds(400);
    internal static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(60);
    internal const int TailChars = 300;

    private static readonly Regex SecretPrompt = new(
        @"(password(\s+for\s+\S+)?|passphrase|passwd|secret|token|\botp\b|2fa|mfa|verification\s*code|\bpin\b)\s*[:：]?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ConfirmPrompt = new(
        @"(are you sure|yes/no|\by/n\b|continue\?|proceed\?)\s*[\[\(]?.{0,20}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex TextPrompt = new(
        @"(username|login|hostkey|fingerprint|enter\s+\w+)\s*[:：]?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] TtySecretPrograms =
        ["ssh", "scp", "sftp", "passwd", "su", "login", "chsh", "chfn"];

    private static readonly string[] StdinSecretPrograms =
        ["mysql", "psql", "gh", "aws", "az", "gcloud", "vault", "openssl", "gpg", "python", "python3", "node", "ruby"];

    /// <summary>Hint-only pre-exec classification. First-token basename match; never blocks.</summary>
    public static InteractiveHint Classify(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return InteractiveHint.None;
        var text = command.TrimStart();
        if (text.Contains("/dev/tty", StringComparison.Ordinal) || text.Contains("stty ", StringComparison.Ordinal))
            return InteractiveHint.TtyRisk;

        var first = FirstToken(text).ToLowerInvariant();
        first = first.StartsWith("sudo", StringComparison.Ordinal) ? "sudo" : System.IO.Path.GetFileName(first);
        if (TtySecretPrograms.Contains(first, StringComparer.Ordinal))
            return InteractiveHint.TtySecret;
        if (first is "sudo" or "doas" or "su" || text.Contains("read -p", StringComparison.Ordinal) || text.Contains("read -s", StringComparison.Ordinal))
            return InteractiveHint.StdinSecret;
        if (StdinSecretPrograms.Contains(first, StringComparer.Ordinal))
            return InteractiveHint.StdinSecret;
        if (text.Contains(" -i", StringComparison.Ordinal) && first is "rm" or "cp" or "mv")
            return InteractiveHint.StdinConfirm;
        return InteractiveHint.None;
    }

    /// <summary>Classify an output tail. Secret wins on ambiguity (safe direction).</summary>
    public static PromptKind ClassifyPrompt(string tail)
    {
        if (string.IsNullOrEmpty(tail))
            return PromptKind.None;
        var window = tail.Length > TailChars ? tail[^TailChars..] : tail;
        if (SecretPrompt.IsMatch(window))
            return PromptKind.Secret;
        if (ConfirmPrompt.IsMatch(window))
            return PromptKind.Confirm;
        if (TextPrompt.IsMatch(window))
            return PromptKind.Text;
        return PromptKind.None;
    }

    public static string Excerpt(string tail, int max = 200)
    {
        var window = tail.Length > max ? tail[^max..] : tail;
        return window.Trim();
    }

    private static string FirstToken(string text)
    {
        var t = text.TrimStart('(', '{', ' ', '\t');
        if (t.StartsWith("sudo", StringComparison.Ordinal))
            return "sudo";
        var end = t.IndexOfAny([' ', '\t', ';', '&', '|']);
        var token = (end < 0 ? t : t[..end]).Trim('\'', '"');
        return token.Length == 0 ? text : token;
    }
}
