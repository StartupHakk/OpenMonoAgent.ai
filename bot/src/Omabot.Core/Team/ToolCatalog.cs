// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Omabot.Team;

/// <summary>OMA built-in tool names (from OMA main) and the roster MCP tools.</summary>
public static partial class ToolCatalog
{
    public static readonly string[] OmaTools =
    [
        "Agent", "ApplyPatch", "AskUser", "Bash", "CreatePlan", "EnterPlanMode", "FileEdit", "FileRead", "FileWrite",
        "Glob", "Grep", "HostExec", "ImplementPlan", "ListDirectory", "Lsp", "MemorySave", "Playbook", "Roslyn",
        "TodoWrite", "ToolSearch", "WebFetch", "WebSearch",
    ];

    /// <summary>Tools that only read. The health bot may use only these and read-only roster tools (HEALTH-01).</summary>
    public static readonly string[] ReadOnlyOmaTools = ["FileRead", "Glob", "Grep", "ListDirectory", "ToolSearch", "Lsp"];

    public static readonly string[] WriteTools = ["FileWrite", "FileEdit", "ApplyPatch", "Bash", "HostExec", "MemorySave", "TodoWrite", "ImplementPlan", "Playbook", "Agent"];

    /// <summary>Roster MCP tools served by the shim (server name "roster").</summary>
    public static readonly string[] RosterTools =
    [
        "list_bots", "assign", "consult", "notify", "assignment_status", "ask", "answer", "cancel", "report",
        "memory_update", "memory_read_topic", "memory_forget", "memory_load",
        "routine_list", "routine_create", "routine_update", "routine_delete", "routine_pause", "routine_resume", "routine_next_runs",
        "doctor_results", "gateway_metrics", "room_post", "room_read",
        "approval_answer", "bot_create", "bot_update", "bot_pause", "bot_resume",
    ];

    /// <summary>
    /// Programs a bash allow list or auto rule may never start with (threat
    /// T3): network clients and interpreters that could reach the bot's own
    /// ACP listener or run text as code, plus privilege and container tools.
    /// </summary>
    public static readonly string[] NeverAutoBash =
    [
        "curl", "wget", "nc", "ncat", "netcat", "socat", "telnet", "ssh", "scp", "sftp", "ftp", "rsync", "openssl",
        "python", "python2", "python3", "perl", "ruby", "node", "nodejs", "deno", "bun", "php", "lua", "tclsh",
        "bash", "sh", "zsh", "dash", "ksh", "fish", "env", "xargs", "eval", "exec", "source", "sudo", "su", "doas",
        "docker", "podman", "kubectl", "nsenter", "chroot", "busybox", "oma-bot", "oma-bot-supervisor", "iptables", "nft",
    ];

    /// <summary>True when a bash pattern starts with a program on <see cref="NeverAutoBash"/>.</summary>
    public static bool IsNeverAutoBash(string pattern)
    {
        var first = pattern.TrimStart().Split([' ', '\t'], 2)[0];
        var program = first.Contains('/') ? first[(first.LastIndexOf('/') + 1)..] : first;
        return NeverAutoBash.Contains(program, StringComparer.Ordinal) || program.StartsWith("python", StringComparison.Ordinal);
    }

    public static readonly string[] ReadOnlyRosterTools = ["list_bots", "assignment_status", "doctor_results", "gateway_metrics", "room_read", "memory_read_topic", "memory_load", "routine_list", "routine_next_runs"];

    /// <summary>Tools that post outside the box. Always hard denied for bots (plan 7.6).</summary>
    public static readonly string[] ExternalPostPatterns = ["mcp__x__", "mcp__twitter__", "mcp__facebook__", "mcp__slack__", "mcp__gmail__send", "mcp__email__send"];

    [GeneratedRegex("^mcp__([a-z0-9_-]+)__([A-Za-z0-9_]+)$")]
    private static partial Regex McpPattern();

    public static bool IsKnown(string tool)
    {
        if (Array.IndexOf(OmaTools, tool) >= 0) return true;
        var m = McpPattern().Match(tool);
        if (!m.Success) return false;
        return m.Groups[1].Value != "roster" || Array.IndexOf(RosterTools, m.Groups[2].Value) >= 0;
    }

    public static bool IsReadOnly(string tool)
    {
        if (Array.IndexOf(ReadOnlyOmaTools, tool) >= 0) return true;
        var m = McpPattern().Match(tool);
        return m.Success && m.Groups[1].Value == "roster" && Array.IndexOf(ReadOnlyRosterTools, m.Groups[2].Value) >= 0;
    }

    public static bool IsExternalPost(string tool)
        => ExternalPostPatterns.Any(p => tool.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Tools whose effect can leave the workspace (plan 7.5 "outward action"). A turn that used one is never rerun after a restart.</summary>
    public static readonly string[] OutwardTools = ["HostExec", "Bash", "Playbook", "Agent"];

    public static bool IsOutward(string tool)
    {
        if (Array.IndexOf(OutwardTools, tool) >= 0) return true;
        var m = McpPattern().Match(tool);
        return m.Success && m.Groups[1].Value != "roster";
    }

    public static string? RosterToolName(string tool)
    {
        var m = McpPattern().Match(tool);
        return m.Success && m.Groups[1].Value == "roster" ? m.Groups[2].Value : null;
    }
}
