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
        "list_bots", "assign", "consult", "notify", "assignment_status", "ask", "report",
        "memory_update", "memory_read_topic", "memory_forget", "memory_load",
        "routine_list", "routine_create", "routine_pause", "routine_resume",
        "doctor_results", "gateway_metrics", "room_post", "room_read",
    ];

    public static readonly string[] ReadOnlyRosterTools = ["list_bots", "assignment_status", "doctor_results", "gateway_metrics", "room_read", "memory_read_topic", "memory_load", "routine_list"];

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

    public static string? RosterToolName(string tool)
    {
        var m = McpPattern().Match(tool);
        return m.Success && m.Groups[1].Value == "roster" ? m.Groups[2].Value : null;
    }
}
