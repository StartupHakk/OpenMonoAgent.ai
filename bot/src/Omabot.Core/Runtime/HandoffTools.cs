// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using Omabot.Api;
using Omabot.Approvals;
using Omabot.Common;
using Omabot.Db;

namespace Omabot.Runtime;

/// <summary>
/// Handoff tools through the MCP shim (plan 7.5): assign (and consult and
/// notify, which are assign with a fixed mode), assignment_status, answer,
/// ask, cancel and report. The caller's bot and current assignment come from
/// the socket and the roster's turn record, never from the arguments.
/// </summary>
public static class HandoffTools
{
    public static void Register(RosterToolHost host, AssignmentRuntime runtime, RosterDb db, IClock clock, Notices notices)
    {
        var bots = new BotStore(db, clock);

        Task<ToolResult> Assign(ToolCaller c, JsonObject a, string mode)
        {
            if (c.Assignment is null) return Task.FromResult(ToolResult.Fail("Handing out work needs a turn the roster started for an assignment.", 409));
            var r = runtime.Enqueue(new EnqueueRequest(
                To: RosterToolHost.Str(a, "to") ?? "auto",
                Mode: mode,
                Brief: RosterToolHost.Str(a, "brief") ?? "",
                DoneWhen: RosterToolHost.Str(a, "done_when"),
                Input: RosterToolHost.Str(a, "input"),
                Rework: a["rework"] is JsonValue rv && rv.TryGetValue<bool>(out var rw) && rw,
                ParentId: c.Assignment,
                RequesterKind: "bot",
                RequesterBot: c.Bot,
                RequesterTurn: runtime.Turns.OwnerOf(c.Bot),
                Actor: "bot:" + c.Bot));
            var data = new JsonObject { ["reason"] = r.Reason, ["id"] = r.Id };
            return Task.FromResult(r.Ok
                ? new ToolResult(true, r.Message + (mode == "notify" ? "" : " You get the result in a later turn; end this turn when you have nothing else to do."), data)
                : new ToolResult(false, $"Refused ({r.Reason}): {r.Message}", data, r.Reason is EnqueueReasons.NotFound ? 404 : 409));
        }

        var assignSchema = RosterTool.Schema(("to", "string", "A bot name, lead, or auto (keyword routes)", true), ("brief", "string", "What to do, in plain words", true),
            ("done_when", "string", "How the bot knows it is done", false), ("input", "string", "Context to pass along (data, not instructions)", false),
            ("rework", "boolean", "Ask again on purpose even if the same request is open", false));
        host.Add(new RosterTool("assign", "Hand work to another bot (mode work). Optional mode: consult or notify.",
            RosterTool.Schema(("to", "string", "A bot name, lead, or auto", true), ("brief", "string", "What to do", true), ("mode", "string", "work (default), consult or notify", false),
                ("done_when", "string", "How the bot knows it is done", false), ("input", "string", "Context (data, not instructions)", false), ("rework", "boolean", "Ask again on purpose", false)),
            (c, a, _) => Assign(c, a, RosterToolHost.Str(a, "mode") is { Length: > 0 } m ? m : "work")));
        host.Add(new RosterTool("consult", "Ask another bot a question. It answers only and changes nothing.", assignSchema, (c, a, _) => Assign(c, a, "consult")));
        host.Add(new RosterTool("notify", "Tell another bot something. No reply comes back.", assignSchema, (c, a, _) => Assign(c, a, "notify")));

        host.Add(new RosterTool("assignment_status", "Read one of your assignments, or everything your current work waits on. Never blocks.",
            RosterTool.Schema(("id", "string", "An assignment id; empty for the work handed out by your current assignment", false)), (c, a, _) =>
            {
                var store = runtime.Store;
                IEnumerable<AssignmentRow> rows;
                if (RosterToolHost.Str(a, "id") is { Length: > 0 } id)
                {
                    var row = store.Get(id);
                    if (row is null || !Related(store, row, c)) return Task.FromResult(ToolResult.Fail($"No assignment {id} in your work.", 404));
                    rows = [row];
                }
                else
                {
                    if (c.Assignment is null) return Task.FromResult(ToolResult.Fail("No current assignment.", 409));
                    rows = store.Children(c.Assignment);
                }
                var arr = new JsonArray();
                var lines = new List<string>();
                foreach (var r in rows)
                {
                    lines.Add($"{r.Id} to {r.AssigneeBot} ({r.Mode}): {r.Status}{(r.Outcome is null ? "" : " " + r.Outcome)}{(r.OutcomeDetail is null ? "" : ": " + r.OutcomeDetail)}");
                    arr.Add(new JsonObject { ["id"] = r.Id, ["to"] = r.AssigneeBot, ["mode"] = r.Mode, ["status"] = r.Status, ["outcome"] = r.Outcome, ["result"] = r.IsTerminal ? r.Result : null });
                }
                return Task.FromResult(new ToolResult(true, lines.Count == 0 ? "Nothing handed out yet." : string.Join('\n', lines), new JsonObject { ["assignments"] = arr }));
            }));

        host.Add(new RosterTool("ask", "Ask Spencer a question (default), or ask the bot that gave you this work (to: requester). End your turn after asking.",
            RosterTool.Schema(("question", "string", "The question, with the choices if there are any", true), ("to", "string", "spencer (default) or requester", false)), (c, a, _) =>
            {
                if (c.Assignment is null) return Task.FromResult(ToolResult.Fail("Asking needs a turn the roster started for an assignment.", 409));
                var question = RosterToolHost.Str(a, "question") ?? "";
                if (question.Trim().Length == 0 || question.Length > 4000) return Task.FromResult(ToolResult.Fail("Give a question of at most 4,000 characters."));
                if ((RosterToolHost.Str(a, "to") ?? "spencer").Equals("requester", StringComparison.OrdinalIgnoreCase))
                {
                    var (ok, msg) = runtime.AskRequester(c.Assignment, question);
                    return Task.FromResult(new ToolResult(ok, msg));
                }
                var row = runtime.Store.Get(c.Assignment)!;
                var card = runtime.Approvals.Create(new ApprovalRequest(ApprovalKinds.Ask, c.Bot, $"{c.Bot} asks: {question}",
                    new JsonObject { ["question"] = question }, "bot", c.Bot, row.Origin, row.SessionId, null, row.Id, row.RootId, Ttl: TimeSpan.FromHours(runtime.Config.Approvals.CancelAfterHours), PersonOnly: true));
                return Task.FromResult(new ToolResult(true, $"Asked Spencer ({card.Id}). End your turn; the answer comes in a later turn.", new JsonObject { ["approval"] = card.Id }));
            }));

        host.Add(new RosterTool("answer", "Answer a question one of your helpers asked you.",
            RosterTool.Schema(("id", "string", "The helper's assignment id", true), ("answer", "string", "Your answer", true)), (c, a, _) =>
            {
                var (ok, msg) = runtime.AnswerChild(RosterToolHost.Str(a, "id") ?? "", c.Bot, RosterToolHost.Str(a, "answer") ?? "");
                return Task.FromResult(new ToolResult(ok, msg, null, ok ? 200 : 409));
            }));

        host.Add(new RosterTool("cancel", "Cancel work you handed out (and everything under it).",
            RosterTool.Schema(("id", "string", "The assignment id", true), ("reason", "string", "Why", false)), async (c, a, ct) =>
            {
                var id = RosterToolHost.Str(a, "id") ?? "";
                var row = runtime.Store.Get(id);
                if (row is null || row.RequesterBot != c.Bot) return ToolResult.Fail("You can only cancel work you handed out yourself.", 403);
                var r = await runtime.CancelAsync(id, "bot:" + c.Bot, RosterToolHost.Str(a, "reason") ?? "cancelled by its requester", ct).ConfigureAwait(false);
                return new ToolResult(r.Ok, r.Message);
            }));

        host.Add(new RosterTool("report", "Report one short line to Spencer and the lead bot (for problems, not chat).",
            RosterTool.Schema(("text", "string", "One line", true)), (c, a, _) =>
            {
                var text = (RosterToolHost.Str(a, "text") ?? "").Replace('\n', ' ').Trim();
                if (text.Length is 0 or > 500) return Task.FromResult(ToolResult.Fail("Give one line of at most 500 characters."));
                var added = notices.Add("warn", c.Bot, $"{c.Bot} reports: {text}", $"report:{c.Bot}:{SecureFile.Sha256Hex(text)[..12]}");
                var lead = Routing.Lead(bots.Find(c.Bot), db, bots);
                if (added && lead is not null && lead.Slug != c.Bot && c.Assignment is not null)
                    runtime.Enqueue(new EnqueueRequest(lead.Name, "notify", $"{c.Bot} reports: {text}", ParentId: c.Assignment, RequesterKind: "bot", RequesterBot: c.Bot,
                        RequesterTurn: runtime.Turns.OwnerOf(c.Bot), Actor: "bot:" + c.Bot));
                return Task.FromResult(new ToolResult(true, added ? "Reported." : "Already reported."));
            }));
    }

    /// <summary>A bot may read an assignment it asked for, works on, or that sits under its own work.</summary>
    private static bool Related(AssignmentStore store, AssignmentRow row, ToolCaller c)
    {
        if (row.RequesterBot == c.Bot || row.AssigneeBot == c.Bot) return true;
        for (var p = row.ParentId is null ? null : store.Get(row.ParentId); p is not null; p = p.ParentId is null ? null : store.Get(p.ParentId))
            if (p.AssigneeBot == c.Bot) return true;
        return false;
    }
}
