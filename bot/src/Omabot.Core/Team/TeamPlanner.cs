// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Omabot.Common;
using Omabot.Config;
using Omabot.Db;

namespace Omabot.Team;

public enum ChangeKind { Create, Update, Unchanged, NotInFile }

public sealed record PlanItem(string Kind, string Name, ChangeKind Change, string Detail, BotSpec? Spec = null, long? ExistingId = null, string? Json = null);

public sealed record TeamPlan(IReadOnlyList<PlanItem> Items, IReadOnlyList<string> Warnings)
{
    public int Creates => Items.Count(i => i.Change == ChangeKind.Create);
    public int Updates => Items.Count(i => i.Change == ChangeKind.Update);
    public int Unchanged => Items.Count(i => i.Change == ChangeKind.Unchanged);
    public bool HasChanges => Creates + Updates > 0;

    public string Render(IClock clock)
    {
        var sb = new StringBuilder();
        foreach (var w in Warnings) sb.Append("warning: ").Append(w).Append('\n');
        foreach (var i in Items.Where(i => i.Change != ChangeKind.Unchanged))
        {
            var verb = i.Change switch
            {
                ChangeKind.Create => "create",
                ChangeKind.Update => "update",
                ChangeKind.NotInFile => "not in file (left alone)",
                _ => "unchanged",
            };
            sb.Append($"{i.Kind} \"{i.Name}\": {verb}");
            if (i.Detail.Length > 0) sb.Append(' ').Append(i.Detail);
            sb.Append('\n');
            if (i.Spec is not null && i.Change != ChangeKind.NotInFile)
            {
                foreach (var r in i.Spec.Routines)
                {
                    var next = TeamPlanner.NextFire(r, clock);
                    sb.Append($"routine \"{i.Name}/{r.Name}\": ({r.Describe()}{(next is null ? "" : ", next " + next)})\n");
                }
            }
        }
        if (!HasChanges) sb.Append("No changes.\n");
        else sb.Append($"Plan: {Creates} to create, {Updates} to change, {Unchanged} unchanged, 0 to delete.\n");
        return sb.ToString();
    }
}

/// <summary>Builds the plan and applies it (plan 7.13). Apply never deletes.</summary>
public sealed class TeamPlanner(RosterDb db, RosterConfig config, RosterPaths paths, IClock clock, AuditLog audit)
{
    private readonly BotStore _bots = new(db, clock);
    private readonly BotVersions _versions = new(db, clock);

    public LiveState Live()
    {
        var models = db.Query("SELECT name FROM team_items WHERE kind='model';", r => r.GetString(0));
        foreach (var b in config.Gateway.Backends) models.AddRange(b.Models.Keys);
        return new LiveState(_bots.All().Select(b => b.Spec).ToList(), models);
    }

    public TeamPlan Plan(TeamFile file)
    {
        var resolved = new TeamResolver(config, paths, clock).Resolve(file, Live());
        var items = new List<PlanItem>();
        var rows = _bots.All();
        foreach (var r in resolved.Bots)
        {
            var row = r.Existing is null ? null : rows.First(x => Names.SameName(x.Name, r.Existing.Name));
            if (row is null)
            {
                items.Add(new PlanItem("bot", r.Spec.Name, ChangeKind.Create,
                    $"({r.Spec.Model}, {r.Spec.PermissionProfile}, workspace {r.Spec.Workspace}{(r.Spec.Role != "worker" ? ", role " + r.Spec.Role : "")})", r.Spec));
                continue;
            }
            var diff = Diff(row.Spec, r.Spec);
            if (r.RenamedFrom is not null) diff.Insert(0, $"rename from \"{r.RenamedFrom}\"");
            items.Add(diff.Count == 0
                ? new PlanItem("bot", r.Spec.Name, ChangeKind.Unchanged, "", r.Spec, row.Id)
                : new PlanItem("bot", r.Spec.Name, ChangeKind.Update, string.Join("; ", diff), r.Spec, row.Id));
        }
        foreach (var row in rows.Where(row => !resolved.Bots.Any(r => r.Existing is not null && Names.SameName(r.Existing.Name, row.Name))))
            items.Add(new PlanItem("bot", row.Name, ChangeKind.NotInFile, ""));

        AddItems(items, "model", resolved.Models.Select(m => (m.Nickname, (object)m)));
        AddItems(items, "team", resolved.Teams.Select(t => (t.Name, (object)new { t.Name, t.Lead, Brief = t.Brief?.Text })));
        AddItems(items, "room", resolved.Rooms.Select(r => (r.Name, (object)new { r.Name, r.Team, r.Members, r.Responder, Brief = r.Brief?.Text, r.MaxPostsPerHour })));
        AddItems(items, "route", resolved.Routes.Select(r => (string.Join(",", r.Words.Select(w => w.ToLowerInvariant())) + "->" + r.To, (object)r)));
        if (resolved.BlockedHosts.Count > 0)
            AddItems(items, "blocked_hosts", [("all", (object)resolved.BlockedHosts.OrderBy(h => h, StringComparer.Ordinal).ToList())]);
        if (resolved.Approvers is not null)
            AddItems(items, "approvers", [("all", (object)resolved.Approvers.Select(a => new { a.Bot, a.Kinds }).ToList())]);
        return new TeamPlan(items, resolved.Warnings);
    }

    private static readonly JsonSerializerOptions ItemJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private void AddItems(List<PlanItem> items, string kind, IEnumerable<(string Name, object Value)> values)
    {
        var existing = db.Query("SELECT name, hash FROM team_items WHERE kind=$k;", r => (r.GetString(0), r.GetString(1)), ("$k", kind))
            .ToDictionary(x => x.Item1, x => x.Item2, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            var json = JsonSerializer.Serialize(value, ItemJson);
            var hash = SecureFile.Sha256Hex(json);
            var change = !existing.TryGetValue(name, out var old) ? ChangeKind.Create : old == hash ? ChangeKind.Unchanged : ChangeKind.Update;
            items.Add(new PlanItem(kind, name, change, "", null, null, json));
        }
    }

    internal static List<string> Diff(BotSpec a, BotSpec b)
    {
        var changes = new List<string>();
        void Text(string field, string? x, string? y)
        {
            if ((x ?? "") == (y ?? "")) return;
            var xl = (x ?? "").Split('\n');
            var yl = (y ?? "").Split('\n');
            var changed = Math.Abs(xl.Length - yl.Length) + xl.Zip(yl).Count(p => p.First != p.Second);
            changes.Add($"{field} ({changed} line{(changed == 1 ? "" : "s")} changed)");
        }
        void Val<T>(string field, T x, T y)
        {
            if (!EqualityComparer<T>.Default.Equals(x, y)) changes.Add($"{field} {x} -> {y}");
        }
        void List(string field, List<string> x, List<string> y)
        {
            var add = y.Except(x).ToList();
            var del = x.Except(y).ToList();
            if (add.Count + del.Count == 0) return;
            changes.Add(field + " " + string.Join(" ", add.Select(t => "+" + t).Concat(del.Select(t => "-" + t))));
        }
        Val("name", a.Name, b.Name);
        Val("team", a.Team, b.Team);
        Val("role", a.Role, b.Role);
        Val("title", a.Title, b.Title);
        Text("description", a.Description, b.Description);
        Val("seen_as", a.SeenAs, b.SeenAs);
        Text("personality", a.Personality, b.Personality);
        Text("plan_prompt", a.PlanPrompt, b.PlanPrompt);
        Text("instructions", a.Instructions, b.Instructions);
        Val("model", a.Model, b.Model);
        Val("permission_profile", a.PermissionProfile, b.PermissionProfile);
        List("tools", a.ToolsAllow, b.ToolsAllow);
        List("tools.deny", a.ToolsDeny, b.ToolsDeny);
        List("bash.allow", a.BashAllow, b.BashAllow);
        List("bash.deny", a.BashDeny, b.BashDeny);
        Val("workspace", a.Workspace, b.Workspace);
        Val("shared_workspace", a.SharedWorkspace, b.SharedWorkspace);
        Val("host_exec", a.HostExec, b.HostExec);
        Val("desktop", a.Desktop, b.Desktop);
        Val("web.search", a.WebSearch, b.WebSearch);
        Val("web.scrape", a.WebScrape, b.WebScrape);
        List("web.allow_hosts", a.AllowHosts, b.AllowHosts);
        Val("memory", $"{a.MemoryEnabled}/{a.MemoryMaxLines}/{a.MemoryMaxBytes}/{a.MemoryCapture}/{a.MemoryTidy}/{a.Recall}",
            $"{b.MemoryEnabled}/{b.MemoryMaxLines}/{b.MemoryMaxBytes}/{b.MemoryCapture}/{b.MemoryTidy}/{b.Recall}");
        Val("timezone", a.Timezone, b.Timezone);
        Val("idle_sleep_minutes", a.IdleSleepMinutes, b.IdleSleepMinutes);
        Val("limits", $"{a.LimitMemory}/{a.LimitCpu}/{a.LimitPids}/{a.MaxTurnMinutes}/{a.DailyTokenBudget}",
            $"{b.LimitMemory}/{b.LimitCpu}/{b.LimitPids}/{b.MaxTurnMinutes}/{b.DailyTokenBudget}");
        List("rooms", a.Rooms, b.Rooms);
        List("accepts_from", a.AcceptsFrom, b.AcceptsFrom);
        List("manages", a.Manages, b.Manages);
        List("keywords", a.Keywords, b.Keywords);
        List("auto_allow", a.AutoAllow ?? [], b.AutoAllow ?? []);
        Val("preset", a.Preset ?? "", b.Preset ?? "");
        List("secrets", [.. a.Secrets.Select(kv => kv.Key + "=" + kv.Value)], [.. b.Secrets.Select(kv => kv.Key + "=" + kv.Value)]);
        var ra = JsonSerializer.Serialize(a.Routines);
        var rb = JsonSerializer.Serialize(b.Routines);
        if (ra != rb)
        {
            var names = a.Routines.Select(r => r.Name).Union(b.Routines.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
            changes.Add("routines " + string.Join(" ", names.Where(n =>
                JsonSerializer.Serialize(a.Routines.FirstOrDefault(r => Names.SameName(r.Name, n))) != JsonSerializer.Serialize(b.Routines.FirstOrDefault(r => Names.SameName(r.Name, n))))));
        }
        return changes;
    }

    /// <summary>
    /// Apply a plan step by step. Each step is its own transaction with one
    /// audit row. On failure, stops and reports what was done (exit 1).
    /// </summary>
    public ApplyResult Apply(TeamPlan plan, string actor, Action<BotSpec>? provision = null)
    {
        var done = new List<string>();
        foreach (var item in plan.Items.Where(i => i.Change is ChangeKind.Create or ChangeKind.Update))
        {
            try
            {
                if (item.Kind == "bot" && item.Spec is not null)
                {
                    db.InTransaction((c, tx) =>
                    {
                        if (item.Change == ChangeKind.Create)
                        {
                            var port = _bots.NextPort(c, config.Bots.PortBase, config.Bots.PortCount);
                            _bots.Insert(c, tx, item.Spec, port);
                            audit.Write(c, tx, actor, "bot.create", item.Spec.Name, $"spec {item.Spec.Hash()[..12]} port {port}");
                            _versions.Record(c, tx, item.Spec, actor, "team apply: created");
                        }
                        else
                        {
                            _bots.UpdateSpec(c, tx, item.ExistingId!.Value, item.Spec);
                            audit.Write(c, tx, actor, "bot.update", item.Spec.Name, item.Detail);
                            _versions.Record(c, tx, item.Spec, actor, "team apply: " + item.Detail);
                        }
                    });
                    provision?.Invoke(item.Spec);
                }
                else if (item.Json is not null)
                {
                    db.InTransaction((c, tx) =>
                    {
                        using var cmd = RosterDb.Cmd(c,
                            "INSERT INTO team_items(kind, name, json, hash, updated_at) VALUES ($k,$n,$j,$h,$t) ON CONFLICT(kind, name) DO UPDATE SET json=excluded.json, hash=excluded.hash, updated_at=excluded.updated_at;",
                            ("$k", item.Kind), ("$n", item.Name), ("$j", item.Json), ("$h", SecureFile.Sha256Hex(item.Json)), ("$t", RosterDb.Now(clock)));
                        cmd.Transaction = tx;
                        cmd.ExecuteNonQuery();
                        audit.Write(c, tx, actor, item.Kind + "." + (item.Change == ChangeKind.Create ? "create" : "update"), item.Name, "");
                    });
                }
                done.Add($"{item.Kind} \"{item.Name}\" {(item.Change == ChangeKind.Create ? "created" : "updated")}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return new ApplyResult(false, done, $"{item.Kind} \"{item.Name}\": {Redactor.Shared.Redact(ex.Message)}");
            }
        }
        return new ApplyResult(true, done, null);
    }

    public static string? NextFire(RoutineSpec r, IClock clock)
    {
        try
        {
            var next = Routines.RoutineRules.Next(r, clock.UtcNow, clock.UtcNow);
            return next is null ? null : Routines.RoutineRules.LocalShort(next.Value, r.Timezone);
        }
        catch (Exception ex) when (ex is Cronos.CronFormatException or TimeZoneNotFoundException)
        {
            return null;
        }
    }
}

public sealed record ApplyResult(bool Ok, IReadOnlyList<string> Done, string? Error);
