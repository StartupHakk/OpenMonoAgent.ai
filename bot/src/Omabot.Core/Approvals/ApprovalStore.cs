// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Omabot.Common;
using Omabot.Db;

namespace Omabot.Approvals;

public static class ApprovalKinds
{
    public const string Permission = "permission";
    public const string Playbook = "playbook";
    public const string ToggleMode = "toggle_mode";
    public const string UserInput = "user_input";
    public const string HostExec = "host_exec";
    public const string Plan = "plan";
    public const string Ask = "ask";
    public const string RoutineChange = "routine_change";
    public const string InstructionChange = "instruction_change";
    public const string DesktopAction = "desktop_action";
    public const string FleetJob = "fleet_job";

    public static readonly string[] All = [Permission, Playbook, ToggleMode, UserInput, HostExec, Plan, Ask, RoutineChange, InstructionChange, DesktopAction, FleetJob];

    /// <summary>Kinds that answer a pause in a running ACP turn.</summary>
    public static bool IsPause(string kind) => kind is Permission or Playbook or ToggleMode or UserInput or HostExec or Plan;
}

public static class ApprovalStatus
{
    public const string Pending = "pending";
    public const string Allowed = "allowed";
    public const string Denied = "denied";
    public const string Expired = "expired";
    public const string Cancelled = "cancelled";
    public const string AutoAllowed = "auto_allowed";
    public const string HardDenied = "hard_denied";
}

/// <summary>Who answered. The roster fills this from its own records (CLI user, web login, approver bot), never from a request body.</summary>
public sealed record Approver(string Kind, string Id, string? UserId = null, string? Role = null)
{
    public static Approver Person(string id, string? userId = null, string role = "owner") => new("person", id, userId, role);
    public static Approver Bot(string slug) => new("bot", slug);
    public override string ToString() => Kind + ":" + Id;
}

public sealed record ApprovalRequest(
    string Kind, string Bot, string Summary, JsonObject Payload, string RequesterKind, string Requester, string Origin = "person",
    string? SessionId = null, string? PauseId = null, string? AssignmentId = null, string? RootId = null, string? UserId = null, TimeSpan? Ttl = null,
    string? BoundTo = null, bool PersonOnly = false, string? Action = null);

public sealed record ApprovalRow(
    string Id, string Kind, string Status, string? UserId, string Bot, string? SessionId, string? PauseId, string? AssignmentId, string? RootId,
    string Origin, string Summary, string Payload, string ActionHash, string RequesterKind, string Requester, string? ApproverKind, string? Approver,
    string? Decision, string? Reason, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? DecidedAt, DateTimeOffset? DeliveredAt,
    string? BoundTo = null, bool PersonOnly = false, string? Action = null)
{
    public JsonObject PayloadJson() => JsonNode.Parse(Payload) as JsonObject ?? [];
    public bool IsDecided => Status is ApprovalStatus.Allowed or ApprovalStatus.Denied or ApprovalStatus.AutoAllowed or ApprovalStatus.HardDenied;
    public bool IsAllowed => Status is ApprovalStatus.Allowed or ApprovalStatus.AutoAllowed;
}

public sealed record DecideResult(bool Ok, string Message, ApprovalRow? Row = null);

/// <summary>Decides whether an approver may answer a card. Phase 6 adds the tree, management and approver list rules.</summary>
public interface IApproverPolicy
{
    /// <summary>Null when the approver may answer; otherwise the plain reason for the refusal.</summary>
    string? Refusal(ApprovalRow row, Approver approver);
}

/// <summary>
/// The approval store (plan 7.6). One row per card with the requester and
/// the action hash it is bound to; every change also writes an append-only
/// approval event. A decision is accepted once, before expiry, from an
/// approver the policy allows, and only for the same action hash.
/// </summary>
public sealed class ApprovalStore(RosterDb db, IClock clock, Redactor redactor)
{
    private const string InsertCols = "id, kind, status, user_id, bot, session_id, pause_id, assignment_id, root_id, origin, summary, payload, action_hash, requester_kind, requester, approver_kind, approver, decision, reason, created_at, expires_at, decided_at, delivered_at, bound_to, person_only, action";
    private const string Cols = InsertCols;

    private static DateTimeOffset? T(SqliteDataReader r, string c) => RosterDb.Str(r, c) is { } s ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : null;

    private static ApprovalRow Map(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), RosterDb.Str(r, "user_id"), r.GetString(4), RosterDb.Str(r, "session_id"), RosterDb.Str(r, "pause_id"),
        RosterDb.Str(r, "assignment_id"), RosterDb.Str(r, "root_id"), r.GetString(9), r.GetString(10), r.GetString(11), r.GetString(12), r.GetString(13), r.GetString(14),
        RosterDb.Str(r, "approver_kind"), RosterDb.Str(r, "approver"), RosterDb.Str(r, "decision"), RosterDb.Str(r, "reason"), T(r, "created_at")!.Value, T(r, "expires_at")!.Value,
        T(r, "decided_at"), T(r, "delivered_at"), RosterDb.Str(r, "bound_to"), r.GetInt64(24) != 0, RosterDb.Str(r, "action"));

    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Hash of the exact action: kind, bot, session, pause id and the
    /// canonical payload. An answer presented for a different hash is refused (SEC-23).
    /// </summary>
    public static string ActionHash(string kind, string bot, string? session, string? pauseId, JsonObject payload)
        => SecureFile.Sha256Hex(string.Join("\u001f", kind, bot, session ?? "", pauseId ?? "", Canonical(payload)));

    public static string Canonical(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                return "{" + string.Join(",", o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => JsonSerializer.Serialize(kv.Key) + ":" + Canonical(kv.Value))) + "}";
            case JsonArray a:
                return "[" + string.Join(",", a.Select(Canonical)) + "]";
            case null:
                return "null";
            default:
                return node.ToJsonString();
        }
    }

    public static string NewId() => "apv_" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));

    public ApprovalRow Create(ApprovalRequest r, string status = ApprovalStatus.Pending, string? decidedBy = null, string? reason = null)
    {
        if (Array.IndexOf(ApprovalKinds.All, r.Kind) < 0) throw new ArgumentException($"Unknown approval kind {r.Kind}.");
        Names.RequireSafeSlug(r.Bot, "bot");
        var id = NewId();
        var now = clock.UtcNow;
        var payload = (JsonNode.Parse(redactor.Redact(r.Payload.ToJsonString())) as JsonObject) ?? [];
        var hash = ActionHash(r.Kind, r.Bot, r.SessionId, r.PauseId, r.Payload);
        var decided = status != ApprovalStatus.Pending;
        db.InTransaction((c, tx) =>
        {
            Exec(c, tx, $"""
                INSERT INTO approvals({InsertCols}) VALUES ($id,$k,$st,$u,$b,$s,$p,$a,$root,$o,$sum,$pl,$h,$rk,$rq,$ak,$ap,$dec,$re,$ca,$ex,$da,NULL,$bt,$po,$act);
                """, ("$id", id), ("$k", r.Kind), ("$st", status), ("$u", r.UserId), ("$b", r.Bot), ("$s", r.SessionId), ("$p", r.PauseId), ("$a", r.AssignmentId),
                ("$root", r.RootId), ("$o", r.Origin), ("$sum", redactor.Redact(r.Summary)), ("$pl", payload.ToJsonString()), ("$h", hash), ("$rk", r.RequesterKind),
                ("$rq", r.Requester), ("$ak", decided ? "roster" : null), ("$ap", decided ? decidedBy ?? "policy" : null), ("$dec", decided ? (status is ApprovalStatus.AutoAllowed ? "allow" : "deny") : null),
                ("$re", reason is null ? null : redactor.Redact(reason)), ("$ca", Iso(now)), ("$ex", Iso(now + (r.Ttl ?? DefaultTtl))), ("$da", decided ? Iso(now) : null),
                ("$bt", r.BoundTo), ("$po", r.PersonOnly ? 1 : 0), ("$act", r.Action is null ? null : redactor.Redact(r.Action.Length > 2000 ? r.Action[..2000] : r.Action)));
            Event(c, tx, id, "roster", "created", $"{r.Kind} for {r.Bot} requested by {r.RequesterKind}:{r.Requester}");
            if (decided) Event(c, tx, id, decidedBy ?? "policy", status, reason ?? "");
        });
        return Get(id)!;
    }

    public ApprovalRow? Get(string id) => db.Query($"SELECT {Cols} FROM approvals WHERE id=$i;", Map, ("$i", id)).FirstOrDefault();

    public ApprovalRow? ForPause(string bot, string? session, string pauseId)
        => db.Query($"SELECT {Cols} FROM approvals WHERE bot=$b AND session_id IS $s AND pause_id=$p;", Map, ("$b", bot), ("$s", session), ("$p", pauseId)).FirstOrDefault();

    public IReadOnlyList<ApprovalRow> Pending(string? userId = null)
        => db.Query($"SELECT {Cols} FROM approvals WHERE status='pending' AND ($u IS NULL OR user_id=$u) ORDER BY created_at;", Map, ("$u", userId));

    public IReadOnlyList<ApprovalRow> Recent(int limit = 50, string? userId = null)
        => db.Query($"SELECT {Cols} FROM approvals WHERE ($u IS NULL OR user_id=$u) ORDER BY created_at DESC LIMIT $n;", Map, ("$u", userId), ("$n", limit));

    /// <summary>Decided, allowed or denied, and not yet delivered to the bot or applied.</summary>
    public IReadOnlyList<ApprovalRow> Undelivered()
        => db.Query($"SELECT {Cols} FROM approvals WHERE status IN ('allowed','denied') AND delivered_at IS NULL ORDER BY decided_at;", Map);

    /// <summary>
    /// Records a decision. Refused when the card is not pending, has expired,
    /// is answered by an approver the policy refuses, or is presented with a
    /// different action hash than the one it was created for.
    /// </summary>
    public DecideResult Decide(string id, Approver approver, bool allow, string? reason, string? presentedHash, IApproverPolicy policy, string? value = null)
    {
        var row = Get(id);
        if (row is null) return new(false, $"No approval {id}.");
        if (presentedHash is not null && !SecureFile.FixedTimeEquals(presentedHash, row.ActionHash))
        {
            Audit(id, approver.ToString(), "refused", "answer presented for a different action hash");
            return new(false, "Refused: the answer is bound to a different action.", row);
        }
        if (row.Status != ApprovalStatus.Pending)
        {
            Audit(id, approver.ToString(), "refused", $"card is already {row.Status}");
            return new(false, $"Refused: {id} is already {row.Status}.", row);
        }
        if (row.ExpiresAt <= clock.UtcNow)
        {
            Expire(row, "expired before an answer");
            return new(false, $"Refused: {id} expired at {row.ExpiresAt:yyyy-MM-dd HH:mm} UTC.", Get(id));
        }
        if (policy.Refusal(row, approver) is { } why)
        {
            Audit(id, approver.ToString(), "refused", why);
            return new(false, "Refused: " + why, row);
        }
        var status = allow ? ApprovalStatus.Allowed : ApprovalStatus.Denied;
        var payload = row.PayloadJson();
        if (value is not null) payload["answer"] = redactor.Redact(value.Length > 4000 ? value[..4000] : value);
        var changed = db.InTransaction((c, tx) =>
        {
            var n = Exec(c, tx, "UPDATE approvals SET status=$s, approver_kind=$ak, approver=$ap, decision=$d, reason=$r, decided_at=$t, payload=$pl WHERE id=$i AND status='pending';",
                ("$s", status), ("$ak", approver.Kind), ("$ap", approver.Id), ("$d", allow ? "allow" : "deny"), ("$r", reason is null ? null : redactor.Redact(reason)),
                ("$t", Iso(clock.UtcNow)), ("$pl", payload.ToJsonString()), ("$i", id));
            if (n == 1) Event(c, tx, id, approver.ToString(), status, reason ?? "");
            return n == 1;
        });
        return changed ? new(true, $"{id} {status}.", Get(id)) : new(false, $"Refused: {id} was answered at the same moment by someone else.", Get(id));
    }

    public void MarkDelivered(string id, string detail)
    {
        db.InTransaction((c, tx) =>
        {
            if (Exec(c, tx, "UPDATE approvals SET delivered_at=$t WHERE id=$i AND delivered_at IS NULL;", ("$t", Iso(clock.UtcNow)), ("$i", id)) == 1)
                Event(c, tx, id, "roster", "delivered", detail);
        });
    }

    public void Cancel(string id, string reason)
    {
        db.InTransaction((c, tx) =>
        {
            if (Exec(c, tx, "UPDATE approvals SET status='cancelled', reason=$r, decided_at=$t WHERE id=$i AND status='pending';", ("$r", redactor.Redact(reason)), ("$t", Iso(clock.UtcNow)), ("$i", id)) == 1)
                Event(c, tx, id, "roster", ApprovalStatus.Cancelled, reason);
        });
    }

    private void Expire(ApprovalRow row, string reason)
    {
        db.InTransaction((c, tx) =>
        {
            if (Exec(c, tx, "UPDATE approvals SET status='expired', reason=$r, decided_at=$t WHERE id=$i AND status='pending';", ("$r", reason), ("$t", Iso(clock.UtcNow)), ("$i", row.Id)) == 1)
                Event(c, tx, row.Id, "roster", ApprovalStatus.Expired, reason);
        });
    }

    /// <summary>Expires pending cards past their time. Returns the expired rows.</summary>
    public IReadOnlyList<ApprovalRow> ExpireDue()
    {
        var due = db.Query($"SELECT {Cols} FROM approvals WHERE status='pending' AND expires_at <= $n;", Map, ("$n", Iso(clock.UtcNow)));
        foreach (var r in due) Expire(r, "no answer before the pause timed out");
        return due;
    }

    public void MarkReminded(string id) => db.Exec("UPDATE approvals SET reminded_at=$t WHERE id=$i;", ("$t", Iso(clock.UtcNow)), ("$i", id));

    /// <summary>Expired cards whose expiry has not been acted on yet (a follow-up question, a cancel).</summary>
    public IReadOnlyList<ApprovalRow> ExpiredUndelivered()
        => db.Query($"SELECT {Cols} FROM approvals WHERE status='expired' AND delivered_at IS NULL ORDER BY decided_at;", Map);

    /// <summary>Pending cards created before <paramref name="before"/> that have not been reminded about.</summary>
    public IReadOnlyList<ApprovalRow> PendingForReminder(DateTimeOffset before)
        => db.Query($"SELECT {Cols} FROM approvals WHERE status='pending' AND reminded_at IS NULL AND created_at <= $b ORDER BY created_at;", Map, ("$b", Iso(before)));

    public IReadOnlyList<(string At, string Actor, string Event, string Detail)> Events(string id)
        => db.Query("SELECT at, actor, event, detail FROM approval_events WHERE approval_id=$i ORDER BY id;", r => (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)), ("$i", id));

    public void Audit(string id, string actor, string ev, string detail)
        => db.InTransaction((c, tx) => Event(c, tx, id, actor, ev, detail));

    private void Event(SqliteConnection c, SqliteTransaction tx, string id, string actor, string ev, string detail)
        => Exec(c, tx, "INSERT INTO approval_events(approval_id, at, actor, event, detail) VALUES ($i,$t,$a,$e,$d);",
            ("$i", id), ("$t", Iso(clock.UtcNow)), ("$a", redactor.Redact(actor)), ("$e", ev), ("$d", redactor.Redact(detail)));

    private static int Exec(SqliteConnection c, SqliteTransaction tx, string sql, params (string, object?)[] args)
    {
        using var cmd = RosterDb.Cmd(c, sql, args);
        cmd.Transaction = tx;
        return cmd.ExecuteNonQuery();
    }

    public static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}

/// <summary>The phase 4 policy: people may answer; a bot never answers its own card and answers nothing unless listed.</summary>
public sealed class PersonOnlyPolicy : IApproverPolicy
{
    public static readonly PersonOnlyPolicy Instance = new();

    public string? Refusal(ApprovalRow row, Approver approver)
    {
        if (approver.Kind == "bot" && Names.SameName(approver.Id, row.Bot)) return "a bot never approves its own request.";
        if (approver.Kind != "person") return "only a person may answer this card.";
        return null;
    }
}
