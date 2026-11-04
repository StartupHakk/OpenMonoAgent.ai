// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using Omabot.Api;
using Omabot.Common;
using Omabot.Config;
using Omabot.Db;
using Omabot.Team;

namespace Omabot.Runtime;

/// <summary>
/// head-honcho's bot management tools (plan 7.16, threat T27). They work
/// only for the lead bot, only in a turn the roster started for work a
/// person gave it directly (never routine, outside or bot-to-bot work), and
/// only on worker bots it manages. Creating, editing and resuming always
/// become person-only cards; pausing applies at once. Every refusal is
/// audited (SEC-26).
/// </summary>
public static class BotTools
{
    public static readonly string[] Forbidden = ["HostExec", "mcp__roster__bot_create", "mcp__roster__bot_update", "mcp__roster__bot_pause", "mcp__roster__bot_resume", "mcp__roster__approval_answer"];

    public static void Register(RosterToolHost host, AssignmentRuntime runtime, BotChanges changes, RosterDb db, RosterConfig config, RosterPaths paths, IClock clock, AuditLog audit)
    {
        var bots = new BotStore(db, clock);

        string? Gate(ToolCaller c, string tool, out BotRow lead)
        {
            lead = bots.Find(c.Bot)!;
            string? why = null;
            if (lead is null || lead.Spec.Role != "lead") why = "Only head-honcho manages bots.";
            else if (c.Assignment is null || runtime.Store.Get(c.Assignment) is not { } a) why = "Bot management works only in a turn the roster started for work a person gave you.";
            else if (!a.IsRoot || a.Origin != "person" || a.RequesterKind != "person")
                why = "Bot management works only in runs a person started, never in routine, outside or bot-to-bot work.";
            if (why is not null) audit.Write("bot:" + c.Bot, "bot.manage_refused", tool, why);
            return why;
        }

        string? Target(ToolCaller c, string tool, BotRow lead, BotRow? target)
        {
            string? why = null;
            if (target is null) why = "No such bot.";
            else if (Names.SameName(target.Slug, lead.Slug)) why = "head-honcho cannot change itself; Spencer edits the team file.";
            else if (target.Spec.Role == "health") why = "The health bot is changed only by Spencer.";
            else if (lead.Spec.Manages.Count > 0 && !lead.Spec.Manages.Any(m => Names.SameName(m, target.Name))) why = $"{target.Name} is not on head-honcho's manages list.";
            if (why is not null) audit.Write("bot:" + c.Bot, "bot.manage_refused", tool, why);
            return why;
        }

        ToolResult Card((Approvals.ApprovalRow? Card, string? Problem) r)
            => r.Card is null
                ? ToolResult.Fail(r.Problem ?? "Refused.", r.Problem?.StartsWith("conflict", StringComparison.Ordinal) == true ? 409 : 400)
                : new ToolResult(true, $"Asked Spencer ({r.Card.Id}). Nothing changes until Spencer allows it.", new JsonObject { ["card"] = r.Card.Id });

        host.Add(new RosterTool("bot_update", "Ask Spencer to change a managed bot's title, description, seen_as, personality, instructions, plan_prompt or model. Pass the version from list_bots.",
            RosterTool.Schema(("bot", "string", "The bot to change", true), ("field", "string", "title, description, seen_as, personality, instructions, plan_prompt or model", true),
                ("value", "string", "The new text", true), ("version", "string", "The version list_bots showed", true)),
            (c, a, _) =>
            {
                if (Gate(c, "bot_update", out var lead) is { } g) return Task.FromResult(ToolResult.Fail(g, 403));
                var target = bots.Find(RosterToolHost.Str(a, "bot") ?? "");
                if (Target(c, "bot_update", lead, target) is { } t) return Task.FromResult(ToolResult.Fail(t, 403));
                return Task.FromResult(Card(changes.RequestUpdate(target!, RosterToolHost.Str(a, "field") ?? "", RosterToolHost.Str(a, "value") ?? "", RosterToolHost.Str(a, "version") ?? "",
                    "bot", lead.Slug, "person")));
            }));

        host.Add(new RosterTool("bot_create", "Ask Spencer for a new worker bot. It starts at the ask level with the tools you list (file and command tools all ask).",
            RosterTool.Schema(("name", "string", "The new bot's name", true), ("title", "string", "One line on what it does", true), ("personality", "string", "Who it is and how it works", true),
                ("instructions", "string", "Standing instructions", false), ("model", "string", "Model nickname; empty for head-honcho's", false),
                ("tools", "string", "Comma separated tools, for example FileRead, Grep, FileEdit", false)),
            (c, a, _) =>
            {
                if (Gate(c, "bot_create", out var lead) is { } g) return Task.FromResult(ToolResult.Fail(g, 403));
                var tools = (RosterToolHost.Str(a, "tools") is { Length: > 0 } ts ? ts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : ["FileRead", "Grep", "Glob", "ListDirectory"]).ToList();
                if (tools.FirstOrDefault(x => Forbidden.Contains(x, StringComparer.Ordinal)) is { } bad)
                {
                    audit.Write("bot:" + c.Bot, "bot.manage_refused", "bot_create", $"asked for {bad}");
                    return Task.FromResult(ToolResult.Fail($"{bad} cannot be given by head-honcho; Spencer adds it in the team file.", 403));
                }
                var def = new BotDef
                {
                    Name = RosterToolHost.Str(a, "name") ?? "",
                    Team = lead.Spec.Team,
                    Role = "worker",
                    Title = RosterToolHost.Str(a, "title"),
                    Personality = new TextValue(RosterToolHost.Str(a, "personality") ?? "", null, 0),
                    Instructions = RosterToolHost.Str(a, "instructions") is { Length: > 0 } ins ? new TextValue(ins, null, 0) : null,
                    Model = RosterToolHost.Str(a, "model") is { Length: > 0 } m ? m : lead.Spec.Model,
                    PermissionProfile = "ask",
                    ToolsAllow = tools,
                    HostExec = false,
                    AcceptsFrom = ["person", lead.Name],
                };
                var planner = new TeamPlanner(db, config, paths, clock, audit);
                BotSpec spec;
                try
                {
                    var resolved = new TeamResolver(config, paths, clock).Resolve(new TeamFile { Version = 1, Bots = [def] }, planner.Live());
                    var r = resolved.Bots.Single();
                    if (r.Existing is not null) return Task.FromResult(ToolResult.Fail($"A bot named {def.Name} already exists.", 409));
                    spec = r.Spec;
                }
                catch (TeamFileException ex)
                {
                    return Task.FromResult(ToolResult.Fail("The new bot is not valid: " + string.Join("; ", ex.Errors.Select(e => e.Message)), 400));
                }
                return Task.FromResult(Card(changes.RequestCreate(spec, "bot", lead.Slug, "person")));
            }));

        host.Add(new RosterTool("bot_pause", "Pause a managed bot now (it takes no new work). Only Spencer can resume it, or you can ask with bot_resume.",
            RosterTool.Schema(("bot", "string", "The bot to pause", true), ("reason", "string", "Why, in one line", true)),
            (c, a, _) =>
            {
                if (Gate(c, "bot_pause", out var lead) is { } g) return Task.FromResult(ToolResult.Fail(g, 403));
                var target = bots.Find(RosterToolHost.Str(a, "bot") ?? "");
                if (Target(c, "bot_pause", lead, target) is { } t) return Task.FromResult(ToolResult.Fail(t, 403));
                return Task.FromResult(new ToolResult(true, changes.Pause(target!, "bot:" + lead.Slug, RosterToolHost.Str(a, "reason") ?? "no reason given")));
            }));

        host.Add(new RosterTool("bot_resume", "Ask Spencer to resume a paused bot.",
            RosterTool.Schema(("bot", "string", "The bot to resume", true)),
            (c, a, _) =>
            {
                if (Gate(c, "bot_resume", out var lead) is { } g) return Task.FromResult(ToolResult.Fail(g, 403));
                var target = bots.Find(RosterToolHost.Str(a, "bot") ?? "");
                if (Target(c, "bot_resume", lead, target) is { } t) return Task.FromResult(ToolResult.Fail(t, 403));
                return Task.FromResult(Card(changes.RequestResume(target!, "bot", lead.Slug, "person")));
            }));
    }
}
