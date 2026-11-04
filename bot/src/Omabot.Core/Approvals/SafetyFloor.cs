// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Omabot.Approvals;

/// <summary>
/// The safety floor (plan 7.6): actions that are never approved at any level,
/// for any bot, in any mode, by any rule or person through the roster. A
/// command the checker cannot fully read is "could not read" and is refused,
/// never treated as clear. It sits beside OMA's PermissionEngine and
/// agent-host's own deny list and replaces neither.
/// </summary>
public static partial class SafetyFloor
{
    public sealed record Verdict(bool Refused, string Reason)
    {
        public static readonly Verdict Clear = new(false, "clear");
    }

    private static readonly (Regex Pattern, string Reason)[] Rules =
    [
        (Rm(), "deletes the root, a system folder or a home folder"),
        (Mkfs(), "formats a disk"),
        (DiskWipe(), "writes over or wipes a disk device"),
        (ForkBomb(), "is a fork bomb"),
        (RosterSecrets(), "touches the roster's own secrets, database or hard rules"),
        (EgressOff(), "turns off the egress gate or the firewall"),
        (DockerSock(), "reaches the Docker socket"),
        (ChmodRoot(), "opens up permissions on the root or a system folder"),
        (PipeToShell(), "runs downloaded or decoded text as a program"),
    ];

    [GeneratedRegex(@"(^|[;&|`(\s])(sudo\s+)?rm\s+(-[a-zA-Z]*\s+)*-[a-zA-Z]*[rR][a-zA-Z]*\s+(-[a-zA-Z-]+\s+)*(--no-preserve-root\s+)?(/|/\*|~/?|\$HOME/?|\$\{HOME\}/?|/home(/[^/\s]+)?/?|/root/?|/etc/?|/usr/?|/var/?|/boot/?|/bin/?|/lib/?|/opt/?|/srv/?|\.\.?/?)(\s|$|[;&|)])", RegexOptions.IgnoreCase)]
    private static partial Regex Rm();

    [GeneratedRegex(@"(^|[;&|`(\s])(sudo\s+)?(mkfs(\.[a-z0-9]+)?|mke2fs|mkswap|fdisk|sfdisk|parted|gdisk|wipefs)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Mkfs();

    [GeneratedRegex(@"(\bdd\b[^;&|]*\bof=/dev/(sd|nvme|hd|vd|xvd|mmcblk|disk|mapper)|\bshred\b[^;&|]*/dev/|>\s*/dev/(sd|nvme|hd|vd|xvd|mmcblk)[a-z0-9]*|\bblkdiscard\b)", RegexOptions.IgnoreCase)]
    private static partial Regex DiskWipe();

    [GeneratedRegex(@":\s*\(\s*\)\s*\{[^}]*:\s*\|\s*:\s*&?[^}]*\}\s*;?\s*:|\bwhile\s+true\s*;\s*do\s+[^;]*&\s*done|\bfork\s*\(\s*\)\s*while", RegexOptions.IgnoreCase)]
    private static partial Regex ForkBomb();

    [GeneratedRegex(@"\.omabot/(secrets|roster\.db|roster\.json)|/omabot/(bot|hooks)/|system-prompt\.md|hard-rules\.md|host-bridge\.json|supervisor\.token|inference\.key", RegexOptions.IgnoreCase)]
    private static partial Regex RosterSecrets();

    [GeneratedRegex(@"\b(iptables|ip6tables|nft|ufw|firewall-cmd)\b[^;&|]*(\s-F\b|\s--flush\b|\s-X\b|\sflush\b|\sdisable\b|\s-P\s+\w+\s+ACCEPT\b|delete\b)|\bdocker\s+(network\s+(disconnect|rm)|stop|kill|rm)\b[^;&|]*\b(oma-roster|egress)\b|OMABOT-(INPUT|USER)", RegexOptions.IgnoreCase)]
    private static partial Regex EgressOff();

    [GeneratedRegex(@"docker\.sock|/var/run/docker|\bDOCKER_HOST\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex DockerSock();

    [GeneratedRegex(@"\bchmod\s+(-[a-zA-Z]+\s+)*[0-7]?777\s+(/|/etc|/usr|/var|/home|/root|/bin|/boot)(\s|$|/)|\bchown\s+(-[a-zA-Z]+\s+)*\S+\s+(/|/etc)(\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ChmodRoot();

    [GeneratedRegex(@"\b(curl|wget|fetch)\b[^|]*\|\s*(sudo\s+)?(ba|z|da|k)?sh\b|\bbase64\s+(-d|--decode)\b[^|]*\|\s*(sudo\s+)?(ba|z|da|k)?sh\b|\beval\b|\bsource\s+/dev/(stdin|tcp)|/dev/tcp/", RegexOptions.IgnoreCase)]
    private static partial Regex PipeToShell();

    /// <summary>Checks a command line (HostExec, a Bash permission request, a playbook step). Null or empty is clear.</summary>
    public static Verdict Check(string? command)
    {
        if (string.IsNullOrEmpty(command)) return Verdict.Clear;
        if (CouldNotRead(command) is { } why) return new Verdict(true, "could not read the command: " + why);
        // Line continuations are joined so a split command is read whole.
        var text = command.Replace("\\\n", " ", StringComparison.Ordinal).Replace("\\\r\n", " ", StringComparison.Ordinal);
        foreach (var (pattern, reason) in Rules)
            if (pattern.IsMatch(text)) return new Verdict(true, reason);
        return Verdict.Clear;
    }

    /// <summary>Reasons a command cannot be fully read: control characters, unbalanced quotes, or a length past what is checked.</summary>
    public static string? CouldNotRead(string command)
    {
        if (command.Length > 8192) return "it is longer than 8 KB";
        foreach (var ch in command)
            if (char.IsControl(ch) && ch is not ('\n' or '\t' or '\r')) return "it has control characters";
        if (command.Any(ch => ch is '\u200B' or '\u200C' or '\u200D' or '\u2060' or '\uFEFF' or '\u202E' or '\u202D')) return "it has hidden characters";
        var single = false;
        var dbl = false;
        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (c == '\\' && !single) { i++; continue; }
            if (c == '\'' && !dbl) single = !single;
            else if (c == '"' && !single) dbl = !dbl;
        }
        if (single || dbl) return "its quotes are not balanced";
        if (Regex.Matches(command, @"\$\(").Count != CountClosers(command)) return "its command substitutions are not balanced";
        return null;
    }

    private static int CountClosers(string s)
    {
        // Count a ")" for each "$(" by walking nesting; unmatched opens make the counts differ.
        var depth = 0;
        var closed = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '$' && i + 1 < s.Length && s[i + 1] == '(') { depth++; i++; }
            else if (s[i] == ')' && depth > 0) { depth--; closed++; }
        }
        return closed;
    }
}
