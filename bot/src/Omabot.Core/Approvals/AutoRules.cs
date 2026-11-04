// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Omabot.Team;

namespace Omabot.Approvals;

/// <summary>
/// Team file auto rules (plan 7.6 broker flow): "Tool", "Tool: literal", or
/// for Bash "Bash: prefix *". Grants are matched against the literal text,
/// never by resolving symlinks or folding "..": a command or path with "..",
/// a "." segment, shell operators or a symlink on the way is refused rather
/// than resolved and matched, so it asks.
/// </summary>
public static partial class AutoRules
{
    private static readonly char[] ShellOperators = [';', '&', '|', '`', '$', '(', ')', '<', '>', '\n', '\r', '\\', '"', '\'', '*', '?', '[', ']', '{', '}', '~', '!', '#'];

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public static string ToolOf(string rule) => Split(rule).Tool;

    public static (string Tool, string? Pattern) Split(string rule)
    {
        var i = rule.IndexOf(':');
        if (i < 0) return (rule.Trim(), null);
        var pattern = rule[(i + 1)..].Trim();
        return (rule[..i].Trim(), pattern.Length == 0 ? null : pattern);
    }

    /// <summary>Why a rule cannot be used, or null. Checked at team apply time and again by the broker.</summary>
    public static string? Problem(string rule, IReadOnlyCollection<string> alwaysAsk)
    {
        var (tool, pattern) = Split(rule);
        if (tool == "HostExec") return "host commands always ask; HostExec cannot have an auto rule";
        if (tool == "Playbook") return "playbooks always ask";
        if (!ToolCatalog.IsKnown(tool)) return $"unknown tool \"{tool}\"";
        if (ToolCatalog.IsExternalPost(tool)) return "tools that post outside the box are always denied";
        if (alwaysAsk.Contains(tool)) return $"{tool} is listed in approvals.always_ask in roster.json, so no auto rule may answer it";
        if (ToolCatalog.IsReadOnly(tool)) return $"{tool} only reads and never asks";
        if (pattern is null) return tool == "Bash" ? "a Bash rule needs a command, for example Bash: git status" : null;
        if (pattern.Length > 200) return "the pattern is over 200 characters";
        if (HasDotSegment(pattern)) return "a rule may not contain .. or . segments";
        switch (tool)
        {
            case "Bash":
                {
                    var body = pattern.EndsWith(" *", StringComparison.Ordinal) ? pattern[..^2].TrimEnd() : pattern;
                    if (body.Length == 0) return "the command is empty";
                    if (body.IndexOfAny(ShellOperators) >= 0) return "a Bash rule may not contain shell operators, quotes or wildcards (other than a final \" *\")";
                    if (ToolCatalog.IsNeverAutoBash(body)) return "network clients, interpreters and privilege tools always ask";
                    if (SafetyFloor.Check(body).Refused) return "the command is on the safety floor";
                    return null;
                }
            case "FileWrite" or "FileEdit" or "ApplyPatch":
                {
                    var body = pattern.EndsWith("/*", StringComparison.Ordinal) ? pattern[..^2] : pattern;
                    if (body.StartsWith('/') || body.StartsWith('~')) return "paths are relative to the workspace";
                    if (body.IndexOfAny(ShellOperators) >= 0) return "a path rule may not contain wildcards other than a final \"/*\"";
                    return null;
                }
            default:
                return $"{tool} rules take no pattern";
        }
    }

    /// <summary>
    /// The first rule that answers this permission pause, or null.
    /// <paramref name="workspaceHost"/> is the bot's workspace on the host, used to refuse a path through a symlink.
    /// </summary>
    public static string? Match(IReadOnlyList<string>? rules, PauseCard card, IReadOnlyCollection<string> alwaysAsk, string? workspaceHost)
    {
        if (rules is null || rules.Count == 0 || card.Kind != ApprovalKinds.Permission || card.Tool is null) return null;
        foreach (var rule in rules)
        {
            if (Problem(rule, alwaysAsk) is not null) continue;
            var (tool, pattern) = Split(rule);
            if (!string.Equals(tool, card.Tool, StringComparison.Ordinal)) continue;
            if (pattern is null)
            {
                if (tool is "FileWrite" or "FileEdit" or "ApplyPatch" && !PathIsLiteral(FilePath(card.Action), workspaceHost)) continue;
                return rule;
            }
            if (tool == "Bash")
            {
                var cmd = card.Command;
                if (cmd is null || cmd.IndexOfAny(ShellOperators) >= 0 || HasDotSegment(cmd)) continue;
                cmd = Spaces().Replace(cmd.Trim(), " ");
                if (pattern.EndsWith(" *", StringComparison.Ordinal))
                {
                    var prefix = Spaces().Replace(pattern[..^2].Trim(), " ");
                    if (cmd == prefix || cmd.StartsWith(prefix + " ", StringComparison.Ordinal)) return rule;
                }
                else if (cmd == Spaces().Replace(pattern, " ")) return rule;
                continue;
            }
            var path = FilePath(card.Action);
            if (path is null || !PathIsLiteral(path, workspaceHost)) continue;
            if (pattern.EndsWith("/*", StringComparison.Ordinal) ? path.StartsWith(pattern[..^1], StringComparison.Ordinal) : path == pattern) return rule;
        }
        return null;
    }

    /// <summary>A workspace-relative path from OMA's file permission summary ("Modify file: /workspace/x" or the bare path).</summary>
    public static string? FilePath(string summary)
    {
        if (summary.Contains('\n')) return null;
        var s = summary;
        foreach (var prefix in new[] { "Modify file: ", "Create file: ", "Delete file: " })
            if (s.StartsWith(prefix, StringComparison.Ordinal)) s = s[prefix.Length..];
        if (s.StartsWith("/workspace/", StringComparison.Ordinal)) return s["/workspace/".Length..];
        return s.StartsWith('/') ? null : s;
    }

    private static bool HasDotSegment(string s)
    {
        foreach (var part in s.Split(['/', ' '], StringSplitOptions.None))
            if (part is "." or "..") return true;
        return s.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>True when the path has no dot segments and no part of it on the host is a symlink.</summary>
    private static bool PathIsLiteral(string? relative, string? workspaceHost)
    {
        if (relative is null || relative.Length == 0 || HasDotSegment(relative) || relative.IndexOfAny(ShellOperators) >= 0) return false;
        if (workspaceHost is null) return true;
        var current = workspaceHost;
        foreach (var part in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                var info = new FileInfo(current);
                if (info.Exists || Directory.Exists(current))
                {
                    if (info.LinkTarget is not null || new DirectoryInfo(current).LinkTarget is not null) return false;
                }
                else break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        return true;
    }
}
