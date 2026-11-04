// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using Omabot.Api;
using Omabot.Common;
using Omabot.Config;
using Omabot.Db;
using Omabot.Team;

namespace Omabot.Routines;

/// <summary>
/// Routine tools through the MCP shim (plan 7.4): routine_list,
/// routine_next_runs, routine_create, routine_update, routine_delete,
/// routine_pause and routine_resume. A bot only sees and changes its own
/// routines; changes become approval cards.
/// </summary>
public static class RoutineTools
{
    public static void Register(RosterToolHost host, RosterDb db, RosterConfig config, IClock clock, RoutineChanges changes)
    {
        var store = new RoutineStore(db, clock);
        BotRow Bot(ToolCaller c) => new BotStore(db, clock).Find(c.Bot) ?? throw new KeyNotFoundException("Unknown bot.");

        host.Add(new RosterTool("routine_list", "List your routines with their rule, next run and last result.", RosterTool.Schema(), (c, _, _) =>
        {
            var bot = Bot(c);
            var arr = new JsonArray();
            var lines = new List<string>();
            foreach (var r in bot.Spec.Routines)
            {
                var st = store.Find(bot.Slug, r.Name);
                var next = st?.NextFire is { } n ? RoutineRules.LocalShort(n, r.Timezone) : RoutineRules.NextThree(r, clock.UtcNow, clock.UtcNow).Select(t => RoutineRules.LocalShort(t, r.Timezone)).FirstOrDefault();
                var state = !r.Enabled ? "off" : st?.Paused == true ? "paused" : "on";
                lines.Add($"{r.Name}: {r.Describe()}, {state}, next {next ?? "none"}, last {st?.LastStatus ?? "never run"}");
                arr.Add(new JsonObject { ["name"] = r.Name, ["rule"] = r.Describe(), ["state"] = state, ["next"] = next, ["last"] = st?.LastStatus });
            }
            return Task.FromResult(new ToolResult(true, lines.Count == 0 ? "You have no routines." : string.Join('\n', lines), new JsonObject { ["routines"] = arr }));
        }));

        host.Add(new RosterTool("routine_next_runs", "Show the next three times for one of your routines, or for a rule you are thinking about.",
            RosterTool.Schema(("name", "string", "Your routine's name, or leave empty and give a rule", false), ("cron", "string", "Five field cron rule", false),
                ("interval_minutes", "integer", "Every N minutes (5 to 1440)", false), ("at", "string", "One time, YYYY-MM-DD HH:MM", false), ("timezone", "string", "IANA zone", false)),
            (c, a, _) =>
            {
                var bot = Bot(c);
                RoutineSpec spec;
                if (RosterToolHost.Str(a, "name") is { Length: > 0 } name)
                    spec = bot.Spec.Routines.FirstOrDefault(r => Names.SameName(r.Name, name)) ?? throw new KeyNotFoundException($"You have no routine named {name}.");
                else spec = SpecFrom(a, "preview", bot.Spec.Timezone);
                var problems = RoutineRules.Problems(spec, config.Routines.MinIntervalMinutes, clock.UtcNow, newOrReenabled: false);
                if (problems.Count > 0) return Task.FromResult(ToolResult.Fail(string.Join(" ", problems)));
                var next = RoutineRules.NextThree(spec, clock.UtcNow, clock.UtcNow).Select(t => RoutineRules.LocalShort(t, spec.Timezone)).ToList();
                return Task.FromResult(new ToolResult(true, next.Count == 0 ? "No upcoming runs." : $"{spec.Describe()}: " + string.Join(", ", next),
                    new JsonObject { ["next"] = new JsonArray([.. next.Select(n => (JsonNode)n)]) }));
            }));

        JsonObject ChangeSchema(bool nameOnly) => nameOnly
            ? RosterTool.Schema(("name", "string", "Routine name", true))
            : RosterTool.Schema(("name", "string", "Routine name", true), ("cron", "string", "Five field cron rule (minute hour day month weekday)", false),
                ("interval_minutes", "integer", "Every N minutes (5 to 1440)", false), ("at", "string", "One time, YYYY-MM-DD HH:MM local", false),
                ("weekdays", "string", "Monday to Friday at HH:MM", false), ("timezone", "string", "IANA zone; your zone when empty", false),
                ("instructions", "string", "What to do when the checks find something", true),
                ("checks", "array", "Checks in team file form, like [{\"file_changed\":{\"path\":\"docs\"}}]", false),
                ("notify", "string", "errors_only (default) or never", false));

        Task<ToolResult> Change(ToolCaller c, JsonObject a, string action)
        {
            var bot = Bot(c);
            var name = RosterToolHost.Str(a, "name") ?? "";
            var spec = action is "create" or "update" ? SpecFrom(a, name, bot.Spec.Timezone) : null;
            var origin = c.Routine is null ? "person" : "routine";
            var (card, problems) = changes.Request(bot.Slug, action, spec, name, "bot", bot.Slug, origin);
            if (card is null) return Task.FromResult(ToolResult.Fail(string.Join(" ", problems)));
            return Task.FromResult(new ToolResult(true, $"Asked Spencer to approve ({card.Id}): {card.Summary}. Nothing changes until he allows it.",
                new JsonObject { ["approval"] = card.Id }));
        }

        host.Add(new RosterTool("routine_create", "Ask for a new routine. Spencer sees a card with the rule, zone and next three times.", ChangeSchema(false), (c, a, _) => Change(c, a, "create")));
        host.Add(new RosterTool("routine_update", "Ask to change one of your routines (needs Spencer's approval).", ChangeSchema(false), (c, a, _) => Change(c, a, "update")));
        host.Add(new RosterTool("routine_delete", "Ask to delete one of your routines (needs Spencer's approval).", ChangeSchema(true), (c, a, _) => Change(c, a, "delete")));
        host.Add(new RosterTool("routine_resume", "Ask to resume a paused routine (needs Spencer's approval).", ChangeSchema(true), (c, a, _) => Change(c, a, "resume")));
        host.Add(new RosterTool("routine_pause", "Pause one of your routines now. Resuming needs Spencer's approval.", ChangeSchema(true), (c, a, _) =>
        {
            var bot = Bot(c);
            var name = RosterToolHost.Str(a, "name") ?? "";
            if (!bot.Spec.Routines.Any(r => Names.SameName(r.Name, name))) return Task.FromResult(ToolResult.Fail($"You have no routine named {name}.", 404));
            store.Sync([bot]);
            var st = store.Find(bot.Slug, name)!;
            store.SetPaused(st.Id, true);
            new AuditLog(db, clock, Redactor.Shared).Write("bot:" + bot.Slug, "routine.pause", $"{bot.Name}/{name}", "paused by the bot itself");
            return Task.FromResult(new ToolResult(true, $"Paused {name}."));
        }));
    }

    /// <summary>Builds a routine spec from tool arguments. Checks use the team file form.</summary>
    public static RoutineSpec SpecFrom(JsonObject a, string name, string defaultZone)
    {
        var spec = new RoutineSpec
        {
            Name = name,
            Cron = RosterToolHost.Str(a, "cron") is { Length: > 0 } cron ? cron : null,
            At = RosterToolHost.Str(a, "at") is { Length: > 0 } at ? at : null,
            Timezone = RosterToolHost.Str(a, "timezone") is { Length: > 0 } tz ? tz : defaultZone,
            Instructions = RosterToolHost.Str(a, "instructions") ?? "",
            Notify = RosterToolHost.Str(a, "notify") is { Length: > 0 } n ? n : "errors_only",
        };
        if (a["interval_minutes"] is JsonValue iv && iv.TryGetValue<int>(out var minutes)) spec.IntervalMinutes = minutes;
        else if (RosterToolHost.Str(a, "interval_minutes") is { } ivs && int.TryParse(ivs, out var m2)) spec.IntervalMinutes = m2;
        if (RosterToolHost.Str(a, "weekdays") is { Length: > 0 } wd)
            spec.Cron = RoutineRules.WeekdaysCron(wd, out var err) ?? throw new FormatException(err);
        if (a["checks"] is JsonArray checks)
        {
            foreach (var node in checks)
            {
                if (node is JsonValue v && v.TryGetValue<string>(out var simple)) { spec.Checks.Add(new CheckDef { Kind = simple }); continue; }
                if (node is not JsonObject o || o.Count != 1) throw new FormatException("Each check is an object with one kind, like {\"file_changed\":{\"path\":\"docs\"}}.");
                var (kind, args) = o.First();
                var def = new CheckDef { Kind = kind };
                if (args is JsonObject ao)
                    foreach (var (k, val) in ao) def.Args[k] = val is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : val?.ToJsonString() ?? "";
                spec.Checks.Add(def);
            }
        }
        return spec;
    }
}
