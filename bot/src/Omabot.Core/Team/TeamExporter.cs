// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using Omabot.Db;

namespace Omabot.Team;

/// <summary>
/// Writes current state as a team file (plan 7.13 `team export`). Secrets are
/// written as their file or env references only; values are never read.
/// Applying the export again prints "No changes." (REG-04).
/// </summary>
public sealed class TeamExporter(RosterDb db, BotStore bots)
{
    public string Export()
    {
        var sb = new StringBuilder();
        sb.Append("# Exported by oma-bot team export. Secrets appear only as file or env references.\n");
        sb.Append("version: 1\n");

        var models = Items("model");
        if (models.Count > 0)
        {
            sb.Append("models:\n");
            foreach (var (name, json) in models)
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                sb.Append("  ").Append(Q(name)).Append(":\n");
                sb.Append("    upstream_model: ").Append(Q(r.GetProperty("upstream_model").GetString() ?? "")).Append('\n');
                if (r.TryGetProperty("context_size", out var cs) && cs.ValueKind == JsonValueKind.Number) sb.Append("    context_size: ").Append(cs.GetInt32()).Append('\n');
                if (r.TryGetProperty("backend", out var be) && be.ValueKind == JsonValueKind.String) sb.Append("    backend: ").Append(Q(be.GetString()!)).Append('\n');
                if (r.TryGetProperty("labels", out var lb) && lb.GetArrayLength() > 0) sb.Append("    labels: ").Append(L(lb.EnumerateArray().Select(x => x.GetString() ?? ""))).Append('\n');
            }
        }

        var teams = Items("team");
        if (teams.Count > 0)
        {
            sb.Append("teams:\n");
            foreach (var (_, json) in teams)
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                sb.Append("  - name: ").Append(Q(r.GetProperty("name").GetString() ?? "")).Append('\n');
                if (r.GetProperty("lead").ValueKind == JsonValueKind.String) sb.Append("    lead: ").Append(Q(r.GetProperty("lead").GetString()!)).Append('\n');
                if (r.GetProperty("brief").ValueKind == JsonValueKind.String) sb.Append("    brief: ").Append(Q(r.GetProperty("brief").GetString()!)).Append('\n');
            }
        }

        var all = bots.All();
        if (all.Count > 0) sb.Append("bots:\n");
        foreach (var row in all)
        {
            var s = row.Spec;
            sb.Append("  - name: ").Append(Q(s.Name)).Append('\n');
            Opt(sb, "team", s.Team);
            sb.Append("    role: ").Append(s.Role).Append('\n');
            Opt(sb, "title", s.Title);
            Opt(sb, "description", s.Description);
            Opt(sb, "seen_as", s.SeenAs);
            sb.Append("    personality: ").Append(Q(s.Personality)).Append('\n');
            Opt(sb, "plan_prompt", s.PlanPrompt);
            Opt(sb, "instructions", s.Instructions);
            sb.Append("    model: ").Append(Q(s.Model)).Append('\n');
            sb.Append("    permission_profile: ").Append(s.PermissionProfile).Append('\n');
            sb.Append("    tools: { allow: ").Append(L(s.ToolsAllow)).Append(", deny: ").Append(L(s.ToolsDeny)).Append(" }\n");
            sb.Append("    bash: { allow: ").Append(L(s.BashAllow)).Append(", deny: ").Append(L(s.BashDeny)).Append(" }\n");
            sb.Append("    workspace: ").Append(Q(s.Workspace)).Append('\n');
            sb.Append("    shared_workspace: ").Append(B(s.SharedWorkspace)).Append('\n');
            sb.Append("    host_exec: ").Append(B(s.HostExec)).Append('\n');
            sb.Append("    desktop: ").Append(Q(s.Desktop)).Append('\n');
            sb.Append("    web: { search: ").Append(B(s.WebSearch)).Append(", scrape: ").Append(B(s.WebScrape)).Append(", allow_hosts: ").Append(L(s.AllowHosts)).Append(" }\n");
            sb.Append("    memory: { enabled: ").Append(B(s.MemoryEnabled)).Append(", max_lines: ").Append(s.MemoryMaxLines)
                .Append(", max_bytes: ").Append(s.MemoryMaxBytes).Append(", recall: ").Append(B(s.Recall))
                .Append(", upkeep: { capture: ").Append(B(s.MemoryCapture)).Append(", tidy: ").Append(B(s.MemoryTidy)).Append(" } }\n");
            sb.Append("    timezone: ").Append(Q(s.Timezone)).Append('\n');
            sb.Append("    idle_sleep_minutes: ").Append(s.IdleSleepMinutes).Append('\n');
            sb.Append("    limits: { memory: ").Append(Q(s.LimitMemory)).Append(", cpu: ").Append(s.LimitCpu.ToString(CultureInfo.InvariantCulture))
                .Append(", pids: ").Append(s.LimitPids).Append(", max_turn_minutes: ").Append(s.MaxTurnMinutes)
                .Append(", daily_token_budget: ").Append(s.DailyTokenBudget).Append(" }\n");
            sb.Append("    rooms: ").Append(L(s.Rooms)).Append('\n');
            sb.Append("    accepts_from: ").Append(L(s.AcceptsFrom)).Append('\n');
            sb.Append("    manages: ").Append(L(s.Manages)).Append('\n');
            sb.Append("    keywords: ").Append(L(s.Keywords)).Append('\n');
            if (s.AutoAllow is { Count: > 0 }) sb.Append("    auto_allow: ").Append(L(s.AutoAllow)).Append('\n');
            if (s.Preset is { } preset) sb.Append("    preset: ").Append(Q(preset)).Append('\n');
            if (s.Secrets.Count > 0)
            {
                sb.Append("    secrets:\n");
                foreach (var (name, reference) in s.Secrets)
                {
                    var idx = reference.IndexOf(':');
                    sb.Append("      ").Append(name).Append(": { ").Append(reference[..idx]).Append(": ").Append(Q(reference[(idx + 1)..])).Append(" }\n");
                }
            }
            if (s.Routines.Count > 0)
            {
                sb.Append("    routines:\n");
                foreach (var r in s.Routines)
                {
                    sb.Append("      - name: ").Append(Q(r.Name)).Append('\n');
                    if (r.Cron is not null) sb.Append("        cron: ").Append(Q(r.Cron)).Append('\n');
                    if (r.IntervalMinutes is not null) sb.Append("        interval_minutes: ").Append(r.IntervalMinutes).Append('\n');
                    if (r.At is not null) sb.Append("        at: ").Append(Q(r.At)).Append('\n');
                    if (r.Watch is not null) sb.Append("        watch: ").Append(Q(r.Watch)).Append('\n');
                    sb.Append("        timezone: ").Append(Q(r.Timezone)).Append('\n');
                    if (r.Checks.Count > 0)
                    {
                        sb.Append("        checks:\n");
                        foreach (var c in r.Checks)
                        {
                            sb.Append("          - ").Append(c.Kind).Append(": {");
                            sb.Append(string.Join(", ", c.Args.Select(kv => " " + kv.Key + ": " + Q(kv.Value))));
                            sb.Append(" }\n");
                        }
                    }
                    sb.Append("        instructions: ").Append(Q(r.Instructions)).Append('\n');
                    sb.Append("        notify: ").Append(r.Notify).Append('\n');
                    sb.Append("        daily_token_budget: ").Append(r.DailyTokenBudget).Append('\n');
                    sb.Append("        enabled: ").Append(B(r.Enabled)).Append('\n');
                    sb.Append("        session: ").Append(r.Session).Append('\n');
                    sb.Append("        host_exec: ").Append(r.HostExec).Append('\n');
                }
            }
        }

        var routes = Items("route");
        if (routes.Count > 0)
        {
            sb.Append("routes:\n");
            foreach (var (_, json) in routes)
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                sb.Append("  - { words: ").Append(L(r.GetProperty("words").EnumerateArray().Select(x => x.GetString() ?? "")))
                    .Append(", to: ").Append(Q(r.GetProperty("to").GetString() ?? "")).Append(" }\n");
            }
        }

        var rooms = Items("room");
        if (rooms.Count > 0)
        {
            sb.Append("rooms:\n");
            foreach (var (_, json) in rooms)
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                sb.Append("  - name: ").Append(Q(r.GetProperty("name").GetString() ?? "")).Append('\n');
                if (r.GetProperty("team").ValueKind == JsonValueKind.String) sb.Append("    team: ").Append(Q(r.GetProperty("team").GetString()!)).Append('\n');
                sb.Append("    members: ").Append(L(r.GetProperty("members").EnumerateArray().Select(x => x.GetString() ?? ""))).Append('\n');
                sb.Append("    responder: ").Append(r.GetProperty("responder").GetString()).Append('\n');
                if (r.GetProperty("brief").ValueKind == JsonValueKind.String) sb.Append("    brief: ").Append(Q(r.GetProperty("brief").GetString()!)).Append('\n');
                sb.Append("    max_posts_per_hour: ").Append(r.GetProperty("max_posts_per_hour").GetInt32()).Append('\n');
            }
        }

        var blocked = Items("blocked_hosts");
        if (blocked.Count > 0)
        {
            using var doc = JsonDocument.Parse(blocked[0].Json);
            sb.Append("blocked_hosts: ").Append(L(doc.RootElement.EnumerateArray().Select(x => x.GetString() ?? ""))).Append('\n');
        }

        var approvers = Items("approvers");
        if (approvers.Count > 0)
        {
            using var doc = JsonDocument.Parse(approvers[0].Json);
            if (doc.RootElement.GetArrayLength() == 0) sb.Append("approvers: []\n");
            else
            {
                sb.Append("approvers:\n");
                foreach (var a in doc.RootElement.EnumerateArray())
                    sb.Append("  - { bot: ").Append(Q(a.GetProperty("bot").GetString() ?? "")).Append(", kinds: ")
                        .Append(L(a.GetProperty("kinds").EnumerateArray().Select(x => x.GetString() ?? ""))).Append(" }\n");
            }
        }
        return sb.ToString();
    }

    private List<(string Name, string Json)> Items(string kind)
        => db.Query("SELECT name, json FROM team_items WHERE kind=$k ORDER BY name;", r => (r.GetString(0), r.GetString(1)), ("$k", kind));

    private static void Opt(StringBuilder sb, string key, string? value)
    {
        if (value is not null) sb.Append("    ").Append(key).Append(": ").Append(Q(value)).Append('\n');
    }

    /// <summary>YAML double-quoted scalar. JSON string escaping is valid YAML.</summary>
    internal static string Q(string s) => JsonSerializer.Serialize(s, QuoteOptions);

    private static readonly JsonSerializerOptions QuoteOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string L(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(Q)) + "]";

    private static string B(bool b) => b ? "true" : "false";
}
