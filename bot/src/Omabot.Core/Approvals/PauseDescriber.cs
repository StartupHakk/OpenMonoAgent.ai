// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using Omabot.Acp;

namespace Omabot.Approvals;

/// <summary>
/// One pause as the broker sees it: the card kind, a plain summary for the
/// feed, the exact action text (a command, a path, a plan) the safety floor
/// and auto rules read, and the raw event kept for the record.
/// </summary>
public sealed record PauseCard(string Kind, string Summary, string Action, string? Tool, string? Command, bool AsRoot, JsonObject Payload);

public static class PauseDescriber
{
    public static PauseCard Describe(AcpPause p)
    {
        var raw = JsonNode.Parse(p.Payload.GetRawText()) as JsonObject ?? [];
        string S(string n) => raw[n] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
        bool B(string n) => raw[n] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        var payload = new JsonObject
        {
            ["pause"] = p.Kind.ToString(),
            ["pause_id"] = p.Id,
            ["tool"] = p.Tool,
            ["dangerous"] = p.Dangerous,
            ["raw"] = raw.DeepClone(),
        };
        switch (p.Kind)
        {
            case PauseKind.HostExec:
                {
                    var cmd = S("command");
                    var root = B("as_root");
                    payload["as_root"] = root;
                    return new PauseCard(ApprovalKinds.HostExec, $"wants to run on the host (as root: {(root ? "yes" : "no")}): {cmd}", cmd, "HostExec", cmd, root, payload);
                }
            case PauseKind.Permission:
                {
                    var tool = string.IsNullOrEmpty(p.Tool) ? S("tool") : p.Tool;
                    var summary = p.Summary;
                    string? command = null;
                    var asRoot = false;
                    if (tool == "Bash") command = BashCommand(summary);
                    else if (tool == "HostExec")
                    {
                        command = HostCommand(summary, out asRoot);
                        payload["as_root"] = asRoot;
                    }
                    return new PauseCard(ApprovalKinds.Permission, $"wants to run {tool}: {summary}", summary, tool, command, asRoot, payload);
                }
            case PauseKind.Playbook:
                {
                    var name = S("playbookName");
                    var steps = raw["steps"] is JsonArray arr
                        ? string.Join("; ", arr.OfType<JsonObject>().Select(s => s["description"] is JsonValue d && d.TryGetValue<string>(out var t) ? t : ""))
                        : "";
                    return new PauseCard(ApprovalKinds.Playbook, $"wants to run playbook {name}", $"playbook {name}: {steps}", "Playbook", null, false, payload);
                }
            case PauseKind.ToggleMode:
                return new PauseCard(ApprovalKinds.ToggleMode, $"wants to switch from plan to build mode: {S("reason")}", "switch to build mode: " + S("reason"), null, null, false, payload);
            case PauseKind.Plan:
                {
                    var plan = S("plan");
                    return new PauseCard(ApprovalKinds.Plan, "has a plan ready. Approve to let it build; each edit still asks.", plan.Length > 4000 ? plan[..4000] : plan, null, null, false, payload);
                }
            default:
                return new PauseCard(ApprovalKinds.UserInput, "asks: " + S("question"), S("question"), null, null, false, payload);
        }
    }

    /// <summary>
    /// The command of a Bash permission: OMA sends the raw command, or
    /// "Execute: binary args" for a capability prompt. A prompt listing
    /// several capabilities has no single command (null).
    /// </summary>
    public static string? BashCommand(string summary)
    {
        if (summary.Contains('\n')) return null;
        return summary.StartsWith("Execute: ", StringComparison.Ordinal) ? summary["Execute: ".Length..] : summary;
    }

    /// <summary>OMA's HostExec permission summary is "[host] sudo (bg) command".</summary>
    public static string HostCommand(string summary, out bool asRoot)
    {
        var s = summary.StartsWith("[host] ", StringComparison.Ordinal) ? summary["[host] ".Length..] : summary;
        asRoot = s.StartsWith("sudo ", StringComparison.Ordinal);
        if (asRoot) s = s["sudo ".Length..];
        if (s.StartsWith("(bg) ", StringComparison.Ordinal)) s = s["(bg) ".Length..];
        return s;
    }

    /// <summary>Rebuilds the pause from a stored card so a later answer goes to the same pause id.</summary>
    public static AcpPause Rebuild(ApprovalRow row)
    {
        var p = row.PayloadJson();
        var kind = Enum.TryParse<PauseKind>(p["pause"]?.GetValue<string>(), out var k) ? k : PauseKind.UserInput;
        var raw = (p["raw"] ?? p["payload"])?.ToJsonString() ?? "{}";
        var tool = p["tool"] is JsonValue t && t.TryGetValue<string>(out var ts) ? ts : null;
        var dangerous = p["dangerous"] is JsonValue d && d.TryGetValue<bool>(out var db) && db;
        return new AcpPause(kind, row.PauseId ?? "", row.Summary, dangerous, tool, JsonDocument.Parse(raw).RootElement.Clone());
    }
}
