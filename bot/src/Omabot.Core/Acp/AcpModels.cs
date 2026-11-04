// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace Omabot.Acp;

public sealed record AcpDiscovery(string Version, string AgentId, string Status, long UptimeSeconds);

public sealed record AcpSessionInfo(string SessionId, string Model, int TurnCount, bool PlanMode, bool HostEnabled);

public enum PauseKind { Permission, Playbook, ToggleMode, UserInput, HostExec }

/// <summary>A pause the bot raised. Payload is the raw event data, kept for audit and display after redaction.</summary>
public sealed record AcpPause(PauseKind Kind, string Id, string Summary, bool Dangerous, string? Tool, JsonElement Payload)
{
    public static AcpPause? From(SseEvent e)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(e.Data).RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
        string S(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        bool B(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        var id = S("id");
        if (id.Length == 0) return null;
        return e.Name switch
        {
            "permission_request" => new AcpPause(PauseKind.Permission, id, S("summary"), B("dangerous"), S("tool"), root),
            "playbook_permission_request" => new AcpPause(PauseKind.Playbook, id, "Run playbook " + S("playbookName"), true, "Playbook", root),
            "toggle_mode_request" => new AcpPause(PauseKind.ToggleMode, id, S("reason"), false, null, root),
            "user_input_request" when S("kind") == "host_exec" =>
                new AcpPause(PauseKind.HostExec, id, "Host command: " + S("command"), true, "HostExec", root),
            "user_input_request" => new AcpPause(PauseKind.UserInput, id, S("question"), false, null, root),
            _ => null,
        };
    }

    /// <summary>The /turn body that answers this pause.</summary>
    public object Answer(bool allow, string? value = null) => Kind switch
    {
        PauseKind.Permission => new { permission = new { id = Id, decision = allow ? "allow" : "deny", scope = "once" } },
        PauseKind.Playbook => new { playbookPermission = new { id = Id, decision = allow ? "allow" : "deny" } },
        PauseKind.ToggleMode => new { toggle_mode = new { id = Id, decision = allow ? "approve" : "deny" } },
        PauseKind.HostExec => new { user_input = new { id = Id, value = allow ? "HOST_EXEC_APPROVE" : "HOST_EXEC_DENY" } },
        _ => new { user_input = new { id = Id, value = value ?? "" } },
    };
}

public sealed record AcpUsage(int InputTokens, int OutputTokens, int TotalTokens);

public enum TurnEnd { Done, Paused, Error, Busy, ResumeError, StreamCut }

public sealed record AcpTurnResult(TurnEnd End, string Text, AcpPause? Pause, string? Error, AcpUsage? Usage, IReadOnlyList<string> ToolsUsed);
