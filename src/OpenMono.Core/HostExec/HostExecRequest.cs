using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenMono.HostBridge;

/// <summary>
/// A host command requested through the AskUser ACP channel.
/// Wire format (set by playbooks/operator prompts, parsed here):
/// <c>HOST_EXEC:</c> followed by either a raw shell command or a JSON object
/// <c>{"command": "...", "timeout_ms": 60000, "background": false, "as_root": false}</c>.
/// The JSON may carry literal newlines inside the command string (models emit
/// raw heredocs more often than escaped ones) — a lenient fallback accepts
/// them, guarded so a truncated value can never execute.
///
/// Shared kernel type: the host-bridge (bare metal) and the sandbox agent
/// (container side, via the host daemon) both parse the same envelope, so the
/// contract can never drift between surfaces.
/// </summary>
public sealed record HostExecRequest(string Command, int? TimeoutMs, bool Background, bool AsRoot)
{
    public const string Prefix = "HOST_EXEC:";

    /// <summary>
    /// Approve / deny tokens a client sends back as the answer to a HOST_EXEC
    /// pause. Kept here so every surface (TUI card, ACP extension, operator
    /// bridge) uses one vocabulary.
    /// </summary>
    public const string ApproveToken = "HOST_EXEC_APPROVE";
    public const string DenyToken = "HOST_EXEC_DENY";

    public static bool IsApprove(string? value) =>
        value is not null && value.Trim().Equals(ApproveToken, StringComparison.OrdinalIgnoreCase);

    public static bool IsDeny(string? value) =>
        value is not null && value.Trim().Equals(DenyToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The turn-resuming answer for a HOST_EXEC envelope that claims the
    /// prefix but does not parse. Tells the agent exactly how to re-send
    /// (valid JSON with \n escapes, or raw shell, or smaller chunks) instead
    /// of parking the operator in front of a raw JSON blob forever.
    /// </summary>
    public static string MalformedEnvelopeAnswer(string question) =>
        $"HOST_EXEC envelope rejected ({question.Length} chars): it starts with HOST_EXEC: but is not " +
        "a raw shell command and not valid envelope JSON, so nothing ran. Re-send ONE of these shapes: " +
        "(1) HOST_EXEC: {\"command\": \"<shell>\", \"timeout_ms\": 30000} with real JSON inside — escape every " +
        "newline as \\n and every quote as \\\"; " +
        "(2) HOST_EXEC: <raw shell command> with no JSON at all (heredocs welcome); " +
        "(3) split payloads over ~4 KB into several HOST_EXEC calls appending with cat >>. " +
        "Never ask the operator to hand-edit JSON.";

    public static bool TryParse(string question, out HostExecRequest? request)
    {
        request = null;
        if (question is null)
            return false;
        var rest = question.StartsWith(Prefix, StringComparison.Ordinal)
            ? question[Prefix.Length..].Trim()
            : question.Trim();
        if (rest.Length == 0)
            return false;

        if (rest.StartsWith('{'))
        {
            if (TryParseStrict(rest, out request))
                return true;
            return TryParseLenient(rest, out request);
        }

        request = new HostExecRequest(rest, null, false, false);
        return true;
    }

    /// <summary>
    /// True when this question claims to be a HOST_EXEC envelope, whether or
    /// not it parses. The bridge must never park the operator in front of one
    /// of these: parseable ones execute, the rest get an auto-reply so the
    /// turn resumes instead of stalling on PENDING_RESPONSE forever.
    /// </summary>
    public static bool LooksLikeHostExec(string question) =>
        !string.IsNullOrWhiteSpace(question) &&
        question.TrimStart().StartsWith(Prefix, StringComparison.Ordinal);

    private static bool TryParseStrict(string rest, out HostExecRequest? request)
    {
        request = null;
        try
        {
            using var doc = JsonDocument.Parse(rest);
            var root = doc.RootElement;
            if (!root.TryGetProperty("command", out var cmdEl) ||
                cmdEl.GetString() is not { Length: > 0 } command)
                return false;
            int? timeout = root.TryGetProperty("timeout_ms", out var tEl) &&
                tEl.TryGetInt32(out var t) ? t : null;
            var background = root.TryGetProperty("background", out var bEl) &&
                bEl.ValueKind == JsonValueKind.True;
            var asRoot = root.TryGetProperty("as_root", out var rEl) &&
                rEl.ValueKind == JsonValueKind.True;
            request = new HostExecRequest(command, timeout, background, asRoot);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static readonly Regex LenientTail = new(
        @"^\s*(,\s*""(?<key>timeout_ms|background|as_root)""\s*:\s*(?<val>-?\d+|true|false)\s*)*}\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LenientTimeout = new(
        @"""timeout_ms""\s*:\s*(?<t>-?\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LenientFlag = new(
        @"""(?<key>background|as_root)""\s*:\s*(?<v>true|false)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Accepts what strict JSON rejects: a <c>"command"</c> string containing
    /// literal newlines/tabs (raw heredocs). The value is scanned honoring
    /// backslash escapes, and whatever follows the closing quote must be
    /// exactly the known sibling fields plus <c>}</c> — an unescaped quote
    /// inside the command fails that check, so a truncated value can never
    /// execute. Anything else is rejected for the auto-error path.
    /// </summary>
    private static bool TryParseLenient(string rest, out HostExecRequest? request)
    {
        request = null;
        var keyIdx = rest.IndexOf("\"command\"", StringComparison.Ordinal);
        if (keyIdx < 0)
            return false;
        var i = keyIdx + "\"command\"".Length;
        i = SkipWs(rest, i);
        if (i >= rest.Length || rest[i] != ':')
            return false;
        i = SkipWs(rest, i + 1);
        if (i >= rest.Length || rest[i] != '"')
            return false;

        var command = new System.Text.StringBuilder(rest.Length);
        i++;
        var closed = false;
        while (i < rest.Length)
        {
            var ch = rest[i];
            if (ch == '\\' && i + 1 < rest.Length)
            {
                // Honor escapes like strict JSON; anything unknown keeps both chars.
                var next = rest[i + 1];
                command.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'b' => '\b',
                    'f' => '\f',
                    _ => next,
                });
                if (next == 'u' && i + 5 < rest.Length)
                {
                    // Best effort: keep the raw sequence when it is not hex.
                    var hex = rest.Substring(i + 2, 4);
                    if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var cp))
                    {
                        command.Length--;
                        command.Append(char.ConvertFromUtf32(cp));
                        i += 6;
                        continue;
                    }
                }
                i += 2;
                continue;
            }
            if (ch == '"')
            {
                closed = true;
                i++;
                break;
            }
            // Literal newlines/tabs (raw heredoc) are accepted here — this is
            // the whole point of the lenient path. Other C0 controls reject.
            if (ch < 0x20 && ch != '\n' && ch != '\r' && ch != '\t')
                return false;
            command.Append(ch);
            i++;
        }
        if (!closed || command.Length == 0)
            return false;

        var tail = rest[i..];
        var tailMatch = LenientTail.Match(tail);
        if (!tailMatch.Success)
            return false;

        int? timeout = null;
        var timeoutMatch = LenientTimeout.Match(tail);
        if (timeoutMatch.Success && int.TryParse(timeoutMatch.Groups["t"].Value, out var t))
            timeout = t;
        var background = false;
        var asRoot = false;
        foreach (Match flag in LenientFlag.Matches(tail))
        {
            var on = flag.Groups["v"].Value == "true";
            if (flag.Groups["key"].Value == "background")
                background = on;
            else
                asRoot = on;
        }
        request = new HostExecRequest(command.ToString(), timeout, background, asRoot);
        return true;
    }

    private static int SkipWs(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i]))
            i++;
        return i;
    }
}
