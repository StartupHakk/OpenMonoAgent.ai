using System.Text;
using System.Text.RegularExpressions;
using OpenMono.Permissions;

namespace OpenMono.Tools;

public static class BashParser
{

    public static BashParseResult Parse(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new BashParseResult([], [], []);

        var segments = new List<CommandSegment>();
        var redirections = new List<Redirection>();
        var subshells = new List<string>();

        var (processed, extractedSubshells) = ExtractSubshells(command);
        subshells.AddRange(extractedSubshells);

        var rawSegments = SplitOnCompoundOperators(processed);

        foreach (var (raw, op) in rawSegments)
        {

            var (cleaned, segRedirections) = ExtractRedirections(raw);
            redirections.AddRange(segRedirections);

            var (binary, args) = ParseSimpleCommand(cleaned);
            if (!string.IsNullOrWhiteSpace(binary))
            {
                segments.Add(new CommandSegment(binary, args, op));
            }
        }

        foreach (var subshell in subshells.Take(50))
        {
            var subResult = Parse(subshell);
            segments.AddRange(subResult.Segments);
            redirections.AddRange(subResult.Redirections);
        }

        return new BashParseResult(segments, redirections, subshells);
    }

    public static IReadOnlyList<Capability> ToCapabilities(BashParseResult result)
    {
        var caps = new List<Capability>();

        foreach (var seg in result.Segments)
        {
            caps.Add(new ProcessExecCap(seg.Binary, seg.Args));
        }

        foreach (var redir in result.Redirections)
        {
            if (redir.IsInput)
                caps.Add(new FileReadCap(redir.Target));
            else
                caps.Add(new FileWriteCap(redir.Target, redir.IsAppend ? "modify" : "create"));
        }

        return caps;
    }

    public static string? CheckDestructive(BashParseResult result)
    {

        var segments = result.Segments.ToList();
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];

            var reason = CheckSegmentDestructive(seg);
            if (reason is not null)
                return reason;

            if (seg.Operator == CompoundOp.Pipe && i + 1 < segments.Count)
            {
                var nextSeg = segments[i + 1];
                if (IsShellInterpreter(nextSeg.Binary))
                    return $"Pipe to shell interpreter ({nextSeg.Binary}) is a potential injection vector";
            }
        }

        if (segments.Count > 1)
        {
            var lastSeg = segments[^1];
            if (IsShellInterpreter(lastSeg.Binary))
            {

                for (var i = 0; i < segments.Count - 1; i++)
                {
                    if (segments[i].Operator == CompoundOp.Pipe)
                        return $"Pipe to shell interpreter ({lastSeg.Binary}) is a potential injection vector";
                }
            }
        }

        foreach (var redir in result.Redirections)
        {
            if (!redir.IsInput && IsProtectedPath(redir.Target))
                return $"Write redirection to protected path: {redir.Target}";
        }

        return null;
    }

    private static bool IsShellInterpreter(string binary)
    {
        var shellInterpreters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sh", "bash", "zsh", "fish", "csh", "tcsh", "ksh", "dash",
            "/bin/sh", "/bin/bash", "/bin/zsh", "/usr/bin/sh", "/usr/bin/bash"
        };
        return shellInterpreters.Contains(binary);
    }

    private static string? CheckSegmentDestructive(CommandSegment seg)
    {
        var binary = seg.Binary.ToLowerInvariant();
        var args = seg.Args.Select(a => a.ToLowerInvariant()).ToList();
        var fullCmd = $"{binary} {string.Join(" ", args)}".Trim();

        if (binary is "rm")
        {
            var hasRecursiveForce = args.Any(a => a.Contains('r') && a.Contains('f') && a.StartsWith('-'));
            if (hasRecursiveForce)
            {
                var targets = args.Where(a => !a.StartsWith('-')).ToList();
                foreach (var target in targets)
                {
                    if (target is "/" or "~" or "." or "*" or "$home" or "${home}")
                        return $"Destructive rm pattern: rm -rf {target}";
                    if (IsProtectedPath(target))
                        return $"rm -rf targeting protected path: {target}";
                }
            }
        }

        if (fullCmd.Contains(":(){:|:&};:"))
            return "Fork bomb detected";

        if (binary is "dd")
        {
            var ofArg = args.FirstOrDefault(a => a.StartsWith("of="));
            if (ofArg is not null)
            {
                var target = ofArg[3..];
                if (target.StartsWith("/dev/sd") || target.StartsWith("/dev/nvme") || target.StartsWith("/dev/hd"))
                    return $"dd writing to block device: {target}";
            }
        }

        if (binary is "shutdown" or "reboot" or "halt" or "poweroff" or "init")
            return $"System control command: {binary}";

        if (binary is "mkfs" || binary.StartsWith("mkfs."))
            return $"Filesystem creation command: {binary}";

        if (binary is "kill" or "pkill" && args.Contains("-1"))
            return "Kill all processes pattern detected";

        if (binary is "chmod" or "chown")
        {
            if (args.Any(a => a is "/" or "-r" or "-R"))
            {
                var targets = args.Where(a => !a.StartsWith('-')).ToList();
                if (targets.Any(t => t is "/" || IsProtectedPath(t)))
                    return $"{binary} on protected path";
            }
        }

        if (binary is "xargs")
        {
            var safeXargsTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "echo", "printf", "wc", "grep", "head", "tail" };

            var flagsTakingArg = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "-I", "-n", "-P", "-d", "-s", "--max-args", "--max-procs", "--delimiter", "--replace" };
            string? targetCmd = null;
            var argList = seg.Args.ToList();
            for (var i = 0; i < argList.Count; i++)
            {
                if (flagsTakingArg.Contains(argList[i])) { i++; continue; }
                if (argList[i].StartsWith('-')) continue;
                targetCmd = argList[i];
                break;
            }

            if (targetCmd is not null && !safeXargsTargets.Contains(targetCmd))
                return $"xargs with unsafe target '{targetCmd}': only echo/printf/wc/grep/head/tail are permitted. " +
                       "Use an explicit loop or direct command instead.";
        }

        return null;
    }

    private static bool IsProtectedPath(string path)
    {
        var normalized = path.Replace('\\', '/').ToLowerInvariant();

        if (normalized is "/dev/null" or "/dev/stdout" or "/dev/stderr" or "/dev/tty" or "/dev/zero")
            return false;
        if (normalized.StartsWith("/dev/fd/"))
            return false;

        string[] protectedPrefixes =
        [
            "/etc/", "/usr/", "/bin/", "/sbin/", "/boot/",
            "/sys/", "/proc/", "/dev/", "/system/", "/library/"
        ];
        return protectedPrefixes.Any(p => normalized.StartsWith(p) || normalized == p.TrimEnd('/'));
    }

    private static (string Processed, List<string> Subshells) ExtractSubshells(string command)
    {
        var subshells = new List<string>();
        var result = new StringBuilder();
        var i = 0;

        while (i < command.Length)
        {

            if (i < command.Length - 1 && command[i] == '$' && command[i + 1] == '(')
            {
                var (content, endIdx) = ExtractParenContent(command, i + 1);
                if (content is not null)
                {
                    subshells.Add(content);
                    result.Append("__SUBSHELL__");
                    i = endIdx + 1;
                    continue;
                }
            }

            if (command[i] == '`')
            {
                var endTick = command.IndexOf('`', i + 1);
                if (endTick > i)
                {
                    subshells.Add(command[(i + 1)..endTick]);
                    result.Append("__SUBSHELL__");
                    i = endTick + 1;
                    continue;
                }
            }

            if (command[i] == '(' && (i == 0 || command[i - 1] != '$'))
            {
                var (content, endIdx) = ExtractParenContent(command, i);
                if (content is not null)
                {
                    subshells.Add(content);
                    result.Append("__SUBSHELL__");
                    i = endIdx + 1;
                    continue;
                }
            }

            result.Append(command[i]);
            i++;
        }

        return (result.ToString(), subshells);
    }

    private static (string? Content, int EndIndex) ExtractParenContent(string s, int openParen)
    {
        if (openParen >= s.Length || s[openParen] != '(')
            return (null, openParen);

        var depth = 0;
        var inSingleQuote = false;
        var inDoubleQuote = false;

        for (var i = openParen; i < s.Length; i++)
        {
            var c = s[i];

            if (c == '\\' && i + 1 < s.Length)
            {
                i++;
                continue;
            }

            if (c == '\'' && !inDoubleQuote) inSingleQuote = !inSingleQuote;
            else if (c == '"' && !inSingleQuote) inDoubleQuote = !inDoubleQuote;

            if (!inSingleQuote && !inDoubleQuote)
            {
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                        return (s[(openParen + 1)..i], i);
                }
            }
        }

        return (null, s.Length);
    }

    private static List<(string Segment, CompoundOp Operator)> SplitOnCompoundOperators(string command)
    {
        var result = new List<(string, CompoundOp)>();
        var current = new StringBuilder();
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var i = 0;

        while (i < command.Length)
        {
            var c = command[i];

            if (c == '\\' && i + 1 < command.Length)
            {
                current.Append(c);
                current.Append(command[i + 1]);
                i += 2;
                continue;
            }

            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                current.Append(c);
                i++;
                continue;
            }
            if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                current.Append(c);
                i++;
                continue;
            }

            if (!inSingleQuote && !inDoubleQuote)
            {

                if (i + 1 < command.Length && command[i] == '&' && command[i + 1] == '&')
                {
                    result.Add((current.ToString().Trim(), CompoundOp.And));
                    current.Clear();
                    i += 2;
                    continue;
                }
                if (i + 1 < command.Length && command[i] == '|' && command[i + 1] == '|')
                {
                    result.Add((current.ToString().Trim(), CompoundOp.Or));
                    current.Clear();
                    i += 2;
                    continue;
                }
                if (command[i] == ';')
                {
                    result.Add((current.ToString().Trim(), CompoundOp.Sequence));
                    current.Clear();
                    i++;
                    continue;
                }
                if (command[i] == '|')
                {
                    result.Add((current.ToString().Trim(), CompoundOp.Pipe));
                    current.Clear();
                    i++;
                    continue;
                }
            }

            current.Append(c);
            i++;
        }

        var final = current.ToString().Trim();
        if (!string.IsNullOrEmpty(final))
            result.Add((final, CompoundOp.None));

        return result;
    }

    private static (string Cleaned, List<Redirection> Redirections) ExtractRedirections(string segment)
    {
        var redirections = new List<Redirection>();
        var cleaned = segment;

        var redirectPattern = new Regex(
            @"(?<fd>\d*)(>>?|<)(?<target>\S+)",
            RegexOptions.Compiled);

        var matches = redirectPattern.Matches(cleaned);
        foreach (Match m in matches)
        {
            var op = m.Groups[1].Value;
            var target = m.Groups["target"].Value.Trim('"', '\'');

            if (target.StartsWith('&'))
                continue;

            var isInput = op == "<";
            var isAppend = op == ">>";

            redirections.Add(new Redirection(target, isInput, isAppend));
        }

        cleaned = redirectPattern.Replace(cleaned, "").Trim();

        return (cleaned, redirections);
    }

    private static (string Binary, IReadOnlyList<string> Args) ParseSimpleCommand(string segment)
    {
        var tokens = Tokenize(segment);
        if (tokens.Count == 0)
            return ("", []);

        var skip = 0;
        while (skip < tokens.Count && IsEnvVarAssignment(tokens[skip]))
            skip++;

        var effective = tokens.Skip(skip).ToList();
        if (effective.Count == 0)
            return ("", []);

        return (effective[0], effective.Skip(1).ToList());
    }

    private static bool IsEnvVarAssignment(string token)
    {
        var eq = token.IndexOf('=');
        if (eq <= 0) return false;
        var name = token[..eq];
        return name.Length > 0 && char.IsLetter(name[0]) &&
               name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }

    private static List<string> Tokenize(string input)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var i = 0;

        while (i < input.Length)
        {
            var c = input[i];

            if (c == '\\' && i + 1 < input.Length && !inSingleQuote)
            {
                current.Append(input[i + 1]);
                i += 2;
                continue;
            }

            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                i++;
                continue;
            }
            if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                i++;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inSingleQuote && !inDoubleQuote)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        if (current.Length > 0)
            tokens.Add(current.ToString());

        return tokens;
    }
}

public sealed record BashParseResult(
    IReadOnlyList<CommandSegment> Segments,
    IReadOnlyList<Redirection> Redirections,
    IReadOnlyList<string> Subshells);

public sealed record CommandSegment(
    string Binary,
    IReadOnlyList<string> Args,
    CompoundOp Operator);

public sealed record Redirection(string Target, bool IsInput, bool IsAppend);

public enum CompoundOp
{

    None,

    And,

    Or,

    Sequence,

    Pipe
}
