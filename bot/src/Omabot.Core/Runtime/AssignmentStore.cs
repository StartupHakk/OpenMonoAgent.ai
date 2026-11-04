// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Omabot.Common;
using Omabot.Db;

namespace Omabot.Runtime;

public static class AssignmentStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string NeedsInput = "needs_input";
    public const string Resume = "resume";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static readonly string[] Terminal = [Completed, Failed, Cancelled];
    public static readonly string[] Live = [Queued, Running, Waiting, NeedsInput, Resume];
    public static bool IsTerminal(string s) => Array.IndexOf(Terminal, s) >= 0;
}

public static class Outcomes
{
    public const string Ok = "ok";
    public const string Refused = "refused";
    public const string Expired = "expired";
    public const string Budget = "budget";
    public const string Depth = "depth";
    public const string Aborted = "aborted";
    public const string Error = "error";
    public const string Interrupted = "interrupted";
    public const string Stalled = "stalled";
}

public sealed record AssignmentRow(
    string Id, string RootId, string? ParentId, int Depth, string Origin, string? OriginRef, string RequesterKind, string? RequesterBot,
    string? RequesterSession, string? RequesterTurn, string AssigneeBot, string AssigneeSession, string? SessionId, string Mode, string Reply,
    string Brief, string? DoneWhen, string? Input, string DedupeKey, bool Rework, string? JoinedTo, string Status, string? Outcome, string? OutcomeDetail,
    string Level, long BudgetTokens, int BudgetTurns, DateTimeOffset? Deadline, int Executions, int Restarts, int Continuations, string? Guard,
    long TokensUsed, string? Result, string? Evidence, string Priority, long? RoutineRunId, string? LeaseOwner, string? Note, bool Reported,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt)
{
    public bool IsRoot => ParentId is null;
    public bool IsTerminal => AssignmentStatus.IsTerminal(Status);
}

public sealed record AssignmentEvent(long Id, string AssignmentId, string At, string? From, string To, string Actor, string Reason, string Data);

public sealed record InboxRow(long Id, string Bot, string AssignmentId, string Kind, string CreatedAt, string? DeliveredAt, string Payload);

public sealed record OutwardRow(long Id, string Bot, string? AssignmentId, string Tool, string Label, string State, DateTimeOffset StartedAt);

/// <summary>
/// Storage for the handoff runtime (plan 7.5): assignments, the append-only
/// assignment_events trail and the inbox wake lane. Every status change is
/// one transaction that writes the row and its event, so a crash keeps every
/// accepted assignment and the row can be rebuilt from its events.
/// </summary>
public sealed class AssignmentStore(RosterDb db, IClock clock, Redactor redactor)
{
    public const string Cols = "id, root_id, parent_id, depth, origin, origin_ref, requester_kind, requester_bot, requester_session, requester_turn, assignee_bot, assignee_session, session_id, mode, reply, brief, done_when, input, dedupe_key, rework, joined_to, status, outcome, outcome_detail, level, budget_tokens, budget_turns, deadline, executions, restarts, continuations, guard, tokens_used, result, evidence, priority, routine_run_id, lease_owner, note, reported, created_at, updated_at, started_at, finished_at";

    /// <summary>Columns a transition may set. Anything else is a programming error.</summary>
    private static readonly HashSet<string> Settable = new(StringComparer.Ordinal)
    {
        "session_id", "outcome", "outcome_detail", "executions", "restarts", "continuations", "guard", "tokens_used", "result", "evidence",
        "lease_owner", "note", "reported", "started_at", "finished_at", "joined_to", "level",
    };

    public RosterDb Db => db;

    private static DateTimeOffset? T(SqliteDataReader r, string c) => RosterDb.Str(r, c) is { } s ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : null;

    private static AssignmentRow Map(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), RosterDb.Str(r, "parent_id"), (int)r.GetInt64(3), r.GetString(4), RosterDb.Str(r, "origin_ref"), r.GetString(6),
        RosterDb.Str(r, "requester_bot"), RosterDb.Str(r, "requester_session"), RosterDb.Str(r, "requester_turn"), r.GetString(10), r.GetString(11),
        RosterDb.Str(r, "session_id"), r.GetString(13), r.GetString(14), r.GetString(15), RosterDb.Str(r, "done_when"), RosterDb.Str(r, "input"),
        r.GetString(18), r.GetInt64(19) != 0, RosterDb.Str(r, "joined_to"), r.GetString(21), RosterDb.Str(r, "outcome"), RosterDb.Str(r, "outcome_detail"),
        r.GetString(24), r.GetInt64(25), (int)r.GetInt64(26), T(r, "deadline"), (int)r.GetInt64(28), (int)r.GetInt64(29), (int)r.GetInt64(30),
        RosterDb.Str(r, "guard"), r.GetInt64(32), RosterDb.Str(r, "result"), RosterDb.Str(r, "evidence"), r.GetString(35), RosterDb.LongN(r, "routine_run_id"),
        RosterDb.Str(r, "lease_owner"), RosterDb.Str(r, "note"), r.GetInt64(39) != 0, T(r, "created_at")!.Value, T(r, "updated_at")!.Value,
        T(r, "started_at"), T(r, "finished_at"));

    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    public static string NewId() => "a_" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));

    public AssignmentRow? Get(string id) => db.Query($"SELECT {Cols} FROM assignments WHERE id=$i;", Map, ("$i", id)).FirstOrDefault();

    public AssignmentRow? Get(SqliteConnection c, SqliteTransaction tx, string id)
    {
        using var cmd = RosterDb.Cmd(c, $"SELECT {Cols} FROM assignments WHERE id=$i;", ("$i", id));
        cmd.Transaction = tx;
        using var r = cmd.ExecuteReader();
        return r.Read() ? Map(r) : null;
    }

    public IReadOnlyList<AssignmentRow> Tree(string rootId) => db.Query($"SELECT {Cols} FROM assignments WHERE root_id=$r ORDER BY created_at, id;", Map, ("$r", rootId));

    public IReadOnlyList<AssignmentRow> Children(string parentId) => db.Query($"SELECT {Cols} FROM assignments WHERE parent_id=$p ORDER BY created_at, id;", Map, ("$p", parentId));

    public IReadOnlyList<AssignmentRow> JoinedTo(string primaryId) => db.Query($"SELECT {Cols} FROM assignments WHERE joined_to=$p;", Map, ("$p", primaryId));

    public IReadOnlyList<AssignmentRow> ByStatus(params string[] statuses)
        => db.Query($"SELECT {Cols} FROM assignments WHERE status IN ({string.Join(",", statuses.Select((_, i) => "$s" + i))}) ORDER BY created_at, id;",
            Map, [.. statuses.Select((s, i) => ("$s" + i, (object?)s))]);

    public IReadOnlyList<AssignmentRow> Recent(int limit, bool liveOnly)
        => db.Query($"SELECT {Cols} FROM assignments {(liveOnly ? "WHERE status NOT IN ('completed','failed','cancelled')" : "")} ORDER BY created_at DESC LIMIT $n;", Map, ("$n", limit));

    public IReadOnlyList<AssignmentRow> ForBot(string bot, params string[] statuses)
        => db.Query($"SELECT {Cols} FROM assignments WHERE assignee_bot=$b AND status IN ({string.Join(",", statuses.Select((_, i) => "$s" + i))});",
            Map, [("$b", bot), .. statuses.Select((s, i) => ("$s" + i, (object?)s))]);

    /// <summary>Inserts a new assignment and its "enqueued" event in the caller's transaction.</summary>
    public void Insert(SqliteConnection c, SqliteTransaction tx, AssignmentRow a, string actor, string reason, object? data = null)
    {
        Exec(c, tx, $"""
            INSERT INTO assignments({Cols}) VALUES ($id,$root,$parent,$depth,$origin,$oref,$rk,$rb,$rs,$rt,$ab,$as,$sid,$mode,$reply,$brief,$done,$input,$dk,$rw,$jt,$st,$oc,$od,$lv,$bt,$bu,$dl,0,0,0,NULL,0,NULL,NULL,$pr,$rr,$lo,$note,0,$ca,$ca,$sa,NULL);
            """, ("$id", a.Id), ("$root", a.RootId), ("$parent", a.ParentId), ("$depth", a.Depth), ("$origin", a.Origin), ("$oref", a.OriginRef),
            ("$rk", a.RequesterKind), ("$rb", a.RequesterBot), ("$rs", a.RequesterSession), ("$rt", a.RequesterTurn), ("$ab", a.AssigneeBot), ("$as", a.AssigneeSession),
            ("$sid", a.SessionId), ("$mode", a.Mode), ("$reply", a.Reply), ("$brief", redactor.Redact(a.Brief)), ("$done", a.DoneWhen is null ? null : redactor.Redact(a.DoneWhen)),
            ("$input", a.Input is null ? null : redactor.Redact(a.Input)), ("$dk", a.DedupeKey), ("$rw", a.Rework ? 1 : 0), ("$jt", a.JoinedTo), ("$st", a.Status),
            ("$oc", a.Outcome), ("$od", a.OutcomeDetail), ("$lv", a.Level), ("$bt", a.BudgetTokens), ("$bu", a.BudgetTurns), ("$dl", a.Deadline is null ? null : Iso(a.Deadline.Value)),
            ("$pr", a.Priority), ("$rr", a.RoutineRunId), ("$lo", a.LeaseOwner), ("$note", a.Note), ("$ca", Iso(a.CreatedAt)), ("$sa", a.StartedAt is null ? null : Iso(a.StartedAt.Value)));
        Event(c, tx, a.Id, null, a.Status, actor, reason, data);
    }

    /// <summary>
    /// Moves an assignment from one of <paramref name="from"/> to <paramref name="to"/>
    /// and writes the event, in one transaction. Returns false when the row was
    /// not in an allowed state (someone else moved it first).
    /// </summary>
    public bool Transition(string id, string[] from, string to, string actor, string reason, IReadOnlyDictionary<string, object?>? set = null, object? data = null)
        => db.InTransaction((c, tx) => Transition(c, tx, id, from, to, actor, reason, set, data));

    public bool Transition(SqliteConnection c, SqliteTransaction tx, string id, string[] from, string to, string actor, string reason, IReadOnlyDictionary<string, object?>? set = null, object? data = null)
    {
        var current = Get(c, tx, id);
        if (current is null || Array.IndexOf(from, current.Status) < 0) return false;
        var args = new List<(string, object?)> { ("$id", id), ("$to", to), ("$t", Iso(clock.UtcNow)) };
        var sets = new List<string> { "status=$to", "updated_at=$t" };
        if (AssignmentStatus.IsTerminal(to) && (set is null || !set.ContainsKey("finished_at"))) sets.Add("finished_at=$t");
        foreach (var (k, v) in set ?? new Dictionary<string, object?>())
        {
            if (!Settable.Contains(k)) throw new ArgumentException($"assignments.{k} cannot be set by a transition.");
            sets.Add($"{k}=${k}");
            args.Add(("$" + k, v is string s && k is "result" or "outcome_detail" or "evidence" or "note" ? redactor.Redact(s) : v));
        }
        var fromList = string.Join(",", from.Select((_, i) => "$f" + i));
        args.AddRange(from.Select((f, i) => ("$f" + i, (object?)f)));
        var n = Exec(c, tx, $"UPDATE assignments SET {string.Join(", ", sets)} WHERE id=$id AND status IN ({fromList});", [.. args]);
        if (n != 1) return false;
        Event(c, tx, id, current.Status, to, actor, reason, data);
        return true;
    }

    /// <summary>Sets columns without a status change (counters, evidence). Still writes an event so the trail is complete.</summary>
    public void Update(string id, string actor, string reason, IReadOnlyDictionary<string, object?> set, object? data = null)
    {
        db.InTransaction((c, tx) =>
        {
            var current = Get(c, tx, id) ?? throw new KeyNotFoundException(id);
            var args = new List<(string, object?)> { ("$id", id), ("$t", Iso(clock.UtcNow)) };
            var sets = new List<string> { "updated_at=$t" };
            foreach (var (k, v) in set)
            {
                if (!Settable.Contains(k)) throw new ArgumentException($"assignments.{k} cannot be set.");
                sets.Add($"{k}=${k}");
                args.Add(("$" + k, v is string s && k is "result" or "outcome_detail" or "evidence" or "note" ? redactor.Redact(s) : v));
            }
            Exec(c, tx, $"UPDATE assignments SET {string.Join(", ", sets)} WHERE id=$id;", [.. args]);
            Event(c, tx, id, current.Status, current.Status, actor, reason, data);
        });
    }

    public void Event(SqliteConnection c, SqliteTransaction tx, string id, string? from, string to, string actor, string reason, object? data)
        => Exec(c, tx, "INSERT INTO assignment_events(assignment_id, at, from_status, to_status, actor, reason, data) VALUES ($a,$t,$f,$to,$ac,$r,$d);",
            ("$a", id), ("$t", Iso(clock.UtcNow)), ("$f", from), ("$to", to), ("$ac", redactor.Redact(actor)), ("$r", redactor.Redact(reason)),
            ("$d", redactor.Redact(data is null ? "{}" : data is string ds ? ds : JsonSerializer.Serialize(data))));

    public IReadOnlyList<AssignmentEvent> Events(string id)
        => db.Query("SELECT id, assignment_id, at, from_status, to_status, actor, reason, data FROM assignment_events WHERE assignment_id=$a ORDER BY id;", MapEvent, ("$a", id));

    public IReadOnlyList<AssignmentEvent> TreeEvents(string rootId)
        => db.Query("""
            SELECT e.id, e.assignment_id, e.at, e.from_status, e.to_status, e.actor, e.reason, e.data FROM assignment_events e
            JOIN assignments a ON a.id = e.assignment_id WHERE a.root_id=$r ORDER BY e.id;
            """, MapEvent, ("$r", rootId));

    private static AssignmentEvent MapEvent(SqliteDataReader r)
        => new(r.GetInt64(0), r.GetString(1), r.GetString(2), RosterDb.Str(r, "from_status"), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7));

    /// <summary>Adds an inbox row. new_work and child_done are unique per assignment: one report, at most one wake (I2).</summary>
    public bool AddInbox(SqliteConnection c, SqliteTransaction tx, string bot, string assignmentId, string kind, object payload)
        => Exec(c, tx, "INSERT OR IGNORE INTO inbox(bot, assignment_id, kind, created_at, payload) VALUES ($b,$a,$k,$t,$p);",
            ("$b", bot), ("$a", assignmentId), ("$k", kind), ("$t", Iso(clock.UtcNow)), ("$p", redactor.Redact(JsonSerializer.Serialize(payload)))) == 1;

    public IReadOnlyList<InboxRow> Inbox(string? bot, bool undeliveredOnly)
        => db.Query($"SELECT id, bot, assignment_id, kind, created_at, delivered_at, payload FROM inbox WHERE ($b IS NULL OR bot=$b){(undeliveredOnly ? " AND delivered_at IS NULL" : "")} ORDER BY id;",
            r => new InboxRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), RosterDb.Str(r, "delivered_at"), r.GetString(6)), ("$b", bot));

    public void MarkInboxDelivered(string assignmentId, string kind)
        => db.Exec("UPDATE inbox SET delivered_at=$t WHERE assignment_id=$a AND kind=$k AND delivered_at IS NULL;", ("$t", Iso(clock.UtcNow)), ("$a", assignmentId), ("$k", kind));

    /// <summary>Marks the parent's child reports read. Open questions stay open until they are answered.</summary>
    public void MarkChildReportsDelivered(string parentId)
        => db.Exec("UPDATE inbox SET delivered_at=$t WHERE kind='child_done' AND delivered_at IS NULL AND assignment_id IN (SELECT id FROM assignments WHERE parent_id=$p);",
            ("$t", Iso(clock.UtcNow)), ("$p", parentId));

    /// <summary>Children whose report the parent has not read yet.</summary>
    public IReadOnlyList<AssignmentRow> UnreadReports(string parentId)
        => db.Query($"""
            SELECT {Cols} FROM assignments WHERE parent_id=$p AND id IN (SELECT assignment_id FROM inbox WHERE kind='child_done' AND delivered_at IS NULL) ORDER BY created_at, id;
            """, Map, ("$p", parentId));

    public long RecordOutward(string bot, string? assignment, string? session, string? leaseOwner, string tool, string label, string state)
    {
        using var c = db.Open();
        RosterDb.Exec(c, "INSERT INTO outward_actions(bot, assignment_id, session_id, lease_owner, tool, label, state, started_at) VALUES ($b,$a,$s,$l,$t,$lb,$st,$at);",
            ("$b", bot), ("$a", assignment), ("$s", session), ("$l", leaseOwner), ("$t", tool), ("$lb", redactor.Redact(label.Length > 200 ? label[..200] : label)),
            ("$st", state), ("$at", Iso(clock.UtcNow)));
        return (long)RosterDb.Scalar(c, "SELECT last_insert_rowid();")!;
    }

    public void MarkOutwardDone(long id) => db.Exec("UPDATE outward_actions SET state='done', done_at=$t WHERE id=$i;", ("$t", Iso(clock.UtcNow)), ("$i", id));

    public IReadOnlyList<OutwardRow> Outward(string assignment, DateTimeOffset since)
        => db.Query("SELECT id, bot, assignment_id, tool, label, state, started_at FROM outward_actions WHERE assignment_id=$a AND started_at >= $s ORDER BY id;",
            r => new OutwardRow(r.GetInt64(0), r.GetString(1), RosterDb.Str(r, "assignment_id"), r.GetString(3), r.GetString(4), r.GetString(5),
                DateTimeOffset.Parse(r.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)),
            ("$a", assignment), ("$s", Iso(since)));

    private static int Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] args)
    {
        using var cmd = RosterDb.Cmd(c, sql, args);
        cmd.Transaction = tx;
        return cmd.ExecuteNonQuery();
    }
}
