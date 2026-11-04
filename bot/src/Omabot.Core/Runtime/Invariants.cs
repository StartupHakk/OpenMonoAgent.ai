// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Omabot.Approvals;
using Omabot.Db;

namespace Omabot.Runtime;

/// <summary>
/// The ten runtime invariants (plan 7.5), restated for the roster's tables.
/// Tests call <see cref="Check"/> after every scenario; a non-empty result
/// names each broken invariant.
///   I1  no live assignment without an owner (a queue entry, a held turn, a waited-on child, a parked question)
///   I2  a settled assignment that reports has exactly one report and at most one wake
///   I3  no result is delivered twice; no brief is dispatched twice except by rework
///   I4  every root ends settled once the roster is quiet; a root a restart ended is reported to Spencer
///   I5  nothing claims "approved" without an allow; an expired assignment is never completed
///   I6  the level a turn ran with equals the level on its record
///   I7  every status change has an event, and the row can be rebuilt from its events
///   I8  a restart reruns a cut turn at most once
///   I9  a parent wakes only when its children have settled or one asked it a question
///   I10 the runtime never throws into a turn: refusals are typed results (checked by the tests that feed bad input)
/// </summary>
public static class Invariants
{
    private static readonly string[] WakeReasons = ["all children settled", "question answered", "requester answered"];

    public static IReadOnlyList<string> Check(RosterDb db, bool quiesced)
    {
        var store = new AssignmentStore(db, Common.SystemClock.Instance, Common.Redactor.Shared);
        var all = store.Recent(int.MaxValue, liveOnly: false);
        var inbox = store.Inbox(null, false);
        var approvals = db.Query("SELECT id, kind, status, assignment_id, approver, decision FROM approvals;",
            r => (Id: r.GetString(0), Kind: r.GetString(1), Status: r.GetString(2), Assignment: RosterDb.Str(r, "assignment_id"), Approver: RosterDb.Str(r, "approver"), Decision: RosterDb.Str(r, "decision")));
        var v = new List<string>();
        var byId = all.ToDictionary(a => a.Id);
        foreach (var a in all)
        {
            var children = all.Where(c => c.ParentId == a.Id).ToList();
            var events = store.Events(a.Id);

            // I1
            switch (a.Status)
            {
                case AssignmentStatus.Waiting when a.JoinedTo is not null:
                    if (!byId.TryGetValue(a.JoinedTo, out var primary) || primary.IsTerminal) v.Add($"I1 {a.Id} waits on {a.JoinedTo}, which is not live");
                    break;
                case AssignmentStatus.Waiting:
                    if (!children.Any(c => c.Reply == "resume" && !c.IsTerminal)) v.Add($"I1 {a.Id} is waiting with no live child to wait on");
                    break;
                case AssignmentStatus.NeedsInput:
                    var parked = approvals.Any(p => p.Assignment == a.Id && p.Status == ApprovalStatus.Pending)
                        || inbox.Any(i => i.AssignmentId == a.Id && i.Kind == "child_question" && i.DeliveredAt is null);
                    if (!parked) v.Add($"I1 {a.Id} needs input but nothing is parked for it");
                    break;
                case AssignmentStatus.Running:
                    if (quiesced) v.Add($"I1 {a.Id} is still running after the roster went quiet");
                    else if (a.LeaseOwner is null) v.Add($"I1 {a.Id} is running without a turn lease");
                    break;
            }

            // I2
            if (a.IsTerminal && a.ParentId is not null && a.Reply is "resume" or "poll")
            {
                var reports = inbox.Count(i => i.AssignmentId == a.Id && i.Kind == "child_done");
                if (reports != 1) v.Add($"I2 {a.Id} has {reports} reports, expected 1");
                if (!a.Reported) v.Add($"I2 {a.Id} settled but is not marked reported");
            }
            if (inbox.Count(i => i.AssignmentId == a.Id && i.Kind == "new_work") > 1) v.Add($"I2 {a.Id} was woken more than once for new work");

            // I4
            if (quiesced && a.IsRoot && !a.IsTerminal && a.Status is not (AssignmentStatus.NeedsInput or AssignmentStatus.Waiting))
                v.Add($"I4 root {a.Id} is {a.Status} after the roster went quiet");

            // I5
            if (a.Status == AssignmentStatus.Completed && a.Outcome == Outcomes.Expired) v.Add($"I5 {a.Id} completed with outcome expired");
            if (a.Status == AssignmentStatus.Completed && approvals.Any(p => p.Assignment == a.Id && p.Status == ApprovalStatus.Expired && ApprovalKinds.IsPause(p.Kind))
                && !approvals.Any(p => p.Assignment == a.Id && p.Status is ApprovalStatus.Allowed or ApprovalStatus.AutoAllowed or ApprovalStatus.Denied or ApprovalStatus.HardDenied))
                v.Add($"I5 {a.Id} completed although its only pause expired");

            // I6
            foreach (var e in events.Where(e => e.Reason == "dispatched"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(e.Data);
                    if (doc.RootElement.TryGetProperty("level", out var lv) && lv.GetString() != a.Level) v.Add($"I6 {a.Id} ran at {lv.GetString()} but its record says {a.Level}");
                }
                catch (JsonException)
                {
                    v.Add($"I6 {a.Id} has an unreadable dispatch event");
                }
            }

            // I7
            if (events.Count == 0 || events[0].From is not null) v.Add($"I7 {a.Id} has no enqueue event");
            else if (events[^1].To != a.Status) v.Add($"I7 {a.Id} is {a.Status} but its last event says {events[^1].To}");
            for (var i = 1; i < events.Count; i++)
                if (events[i].From != events[i - 1].To) v.Add($"I7 {a.Id} event {events[i].Id} starts from {events[i].From} after {events[i - 1].To}");

            // I8
            if (a.Restarts > 1) v.Add($"I8 {a.Id} was rerun after restarts {a.Restarts} times");

            // I9
            foreach (var e in events.Where(e => e.From == AssignmentStatus.Waiting && e.To == AssignmentStatus.Resume))
                if (!WakeReasons.Contains(e.Reason) && !e.Reason.EndsWith("asked a question", StringComparison.Ordinal)) v.Add($"I9 {a.Id} woke for \"{e.Reason}\"");
        }

        // I3
        if (inbox.Where(i => i.Kind == "child_done").GroupBy(i => i.AssignmentId).Any(g => g.Count() > 1)) v.Add("I3 a result was delivered twice");
        foreach (var g in all.Where(a => !a.Rework && a.JoinedTo is null).GroupBy(a => a.DedupeKey))
        {
            var rows = g.OrderBy(a => a.CreatedAt).ToList();
            for (var i = 1; i < rows.Count; i++)
                if (rows[i].CreatedAt - rows[i - 1].CreatedAt < TimeSpan.FromMinutes(10) && rows[i - 1].Status is not (AssignmentStatus.Failed or AssignmentStatus.Cancelled))
                    v.Add($"I3 brief dispatched twice: {rows[i - 1].Id} and {rows[i].Id}");
        }

        // I5 approvals
        foreach (var p in approvals.Where(p => p.Status == ApprovalStatus.Allowed))
            if (p.Approver is null || p.Decision != "allow") v.Add($"I5 approval {p.Id} says allowed without an approver's allow");

        // I4 restart notices
        foreach (var a in all.Where(a => a.IsRoot && a.Outcome == Outcomes.Interrupted))
            if (Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM notices WHERE dedupe_key=$k;", ("$k", "assignment:" + a.Id)) ?? 0L) != 1)
                v.Add($"I4 root {a.Id} ended by a restart but Spencer was not told once");
        return v;
    }
}
