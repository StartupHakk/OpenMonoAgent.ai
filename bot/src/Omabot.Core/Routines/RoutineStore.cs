// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Microsoft.Data.Sqlite;
using Omabot.Common;
using Omabot.Db;
using Omabot.Team;

namespace Omabot.Routines;

public sealed record RoutineStateRow(
    long Id, string Bot, string Name, string SpecHash, bool Paused, bool Orphaned, bool Done, DateTimeOffset Anchor,
    DateTimeOffset? NextFire, DateTimeOffset? LastFire, string? LastStatus, string? WatchFingerprint, DateTimeOffset? WatchRetryAt,
    DateTimeOffset? RunNowAt, string? BudgetNoticeDay);

public sealed record RoutineRunRow(
    long Id, long RoutineId, string Bot, string Routine, DateTimeOffset ScheduledFor, string Trigger, string Status, string? Reason,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, int Checks, int ModelCalls, long Tokens, long Ms, bool Notified, string? Owner, string? AssignmentId);

/// <summary>A routine as the scheduler sees it: the bot row, the spec from the team file and the state row.</summary>
public sealed record ScheduledRoutine(Db.BotRow Bot, RoutineSpec Spec, RoutineStateRow State);

/// <summary>
/// Scheduler state in roster.db (plan 7.4 "Persistence and recovery").
/// Routine rules live in each bot's spec (team file and approved changes);
/// this store keeps when each one last and next fires, runtime pauses and
/// every run. A run row is unique per routine and scheduled time, so one
/// occurrence can never fire twice, whatever restarts in between.
/// </summary>
public sealed class RoutineStore(RosterDb db, IClock clock)
{
    public static class Status
    {
        public const string Running = "running";
        public const string Quiet = "quiet";
        public const string Ok = "ok";
        public const string Error = "error";
        public const string Failed = "failed";
        public const string NeedsInput = "needs_input";
        public const string SkippedOverlap = "skipped_overlap";
        public const string SkippedBudget = "skipped_budget";
        public const string Missed = "missed";
        public static readonly string[] Notifying = [Error, Failed, NeedsInput];
    }

    /// <summary>Occurrence key: UTC to the second, one format everywhere.</summary>
    public static string Key(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static DateTimeOffset? T(SqliteDataReader r, string col)
    {
        var s = RosterDb.Str(r, col);
        return s is null ? null : DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    }

    private const string StateCols = "id, bot, name, spec_hash, paused, orphaned, done, anchor_at, next_fire_at, last_fire_at, last_status, watch_fingerprint, watch_retry_at, run_now_at, budget_notice_day";

    private static RoutineStateRow MapState(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4) != 0, r.GetInt64(5) != 0, r.GetInt64(6) != 0,
        T(r, "anchor_at")!.Value, T(r, "next_fire_at"), T(r, "last_fire_at"), RosterDb.Str(r, "last_status"), RosterDb.Str(r, "watch_fingerprint"),
        T(r, "watch_retry_at"), T(r, "run_now_at"), RosterDb.Str(r, "budget_notice_day"));

    private const string RunCols = "id, routine_id, bot, routine, scheduled_for, trigger, status, reason, started_at, finished_at, checks, model_calls, tokens, ms, notified, owner, assignment_id";

    private static RoutineRunRow MapRun(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), T(r, "scheduled_for")!.Value, r.GetString(5), r.GetString(6),
        RosterDb.Str(r, "reason"), T(r, "started_at"), T(r, "finished_at"), (int)r.GetInt64(10), (int)r.GetInt64(11), r.GetInt64(12), r.GetInt64(13),
        r.GetInt64(14) != 0, RosterDb.Str(r, "owner"), RosterDb.Str(r, "assignment_id"));

    public static string SpecHash(RoutineSpec r) => SecureFile.Sha256Hex(System.Text.Json.JsonSerializer.Serialize(r))[..16];

    /// <summary>
    /// Brings state rows in line with the bots' specs: new routines get a row
    /// (anchored now), changed rules are recomputed, and routines no longer in
    /// any spec are marked orphaned (never deleted, their runs stay).
    /// </summary>
    public IReadOnlyList<ScheduledRoutine> Sync(IEnumerable<Db.BotRow> bots)
    {
        var now = clock.UtcNow;
        var nowS = Key(now);
        var result = new List<ScheduledRoutine>();
        var seen = new HashSet<long>();
        foreach (var bot in bots)
        {
            foreach (var spec in bot.Spec.Routines)
            {
                var hash = SpecHash(spec);
                var row = Find(bot.Slug, spec.Name);
                if (row is null)
                {
                    db.Exec("INSERT INTO routine_state(bot, name, spec_hash, anchor_at, created_at, updated_at) VALUES ($b,$n,$h,$a,$a,$a);",
                        ("$b", bot.Slug), ("$n", spec.Name), ("$h", hash), ("$a", nowS));
                    row = Find(bot.Slug, spec.Name)!;
                }
                else if (row.SpecHash != hash || row.Orphaned)
                {
                    // A changed rule starts counting again from now; a re-enabled one-time routine may fire again.
                    db.Exec("UPDATE routine_state SET spec_hash=$h, orphaned=0, done=0, anchor_at=$a, next_fire_at=NULL, watch_fingerprint=NULL, updated_at=$a WHERE id=$i;",
                        ("$h", hash), ("$a", nowS), ("$i", row.Id));
                    if (row.SpecHash != hash) db.Exec("UPDATE routine_state SET last_fire_at=$a WHERE id=$i AND last_fire_at IS NOT NULL;", ("$a", nowS), ("$i", row.Id));
                    row = Find(bot.Slug, spec.Name)!;
                }
                seen.Add(row.Id);
                result.Add(new ScheduledRoutine(bot, spec, row));
            }
        }
        db.Exec($"UPDATE routine_state SET orphaned=1, updated_at=$t WHERE orphaned=0 AND id NOT IN ({(seen.Count == 0 ? "-1" : string.Join(",", seen))});", ("$t", nowS));
        return result;
    }

    public RoutineStateRow? Find(string bot, string name)
        => db.Query($"SELECT {StateCols} FROM routine_state WHERE bot=$b AND name=$n COLLATE NOCASE;", MapState, ("$b", bot), ("$n", name)).FirstOrDefault();

    public RoutineStateRow? Get(long id) => db.Query($"SELECT {StateCols} FROM routine_state WHERE id=$i;", MapState, ("$i", id)).FirstOrDefault();

    public IReadOnlyList<RoutineStateRow> All() => db.Query($"SELECT {StateCols} FROM routine_state ORDER BY bot, name;", MapState);

    /// <summary>Inserts a run for one occurrence. Returns null when that occurrence already has a row (no double fire).</summary>
    public long? TryStartRun(long routineId, string bot, string routine, DateTimeOffset scheduledFor, string trigger, string status, string? owner = null, string? reason = null)
    {
        var now = RosterDb.Now(clock);
        using var c = db.Open();
        var n = RosterDb.Exec(c, """
            INSERT OR IGNORE INTO routine_runs(routine_id, bot, routine, scheduled_for, trigger, status, reason, started_at, owner)
            VALUES ($r,$b,$n,$s,$t,$st,$re,$at,$o);
            """, ("$r", routineId), ("$b", bot), ("$n", routine), ("$s", Key(scheduledFor)), ("$t", trigger), ("$st", status), ("$re", reason), ("$at", now), ("$o", owner));
        if (n == 0) return null;
        return (long)RosterDb.Scalar(c, "SELECT last_insert_rowid();")!;
    }

    /// <summary>
    /// Closes a run. A model run closes only while still "running", so a turn
    /// finishing after a restart already marked it interrupted cannot rewrite it.
    /// Returns false when the row was not changed.
    /// </summary>
    public bool FinishRun(long runId, string status, string? reason, int checks, int modelCalls, long tokens, long ms, bool notified, bool onlyIfRunning = true)
        => db.Exec($"""
            UPDATE routine_runs SET status=$s, reason=$r, finished_at=$f, checks=$c, model_calls=$m, tokens=$t, ms=$ms, notified=$n WHERE id=$i{(onlyIfRunning ? " AND status='running'" : "")};
            """, ("$s", status), ("$r", reason), ("$f", RosterDb.Now(clock)), ("$c", checks), ("$m", modelCalls), ("$t", tokens), ("$ms", ms), ("$n", notified ? 1 : 0), ("$i", runId)) == 1;

    public void SetRunAssignment(long runId, string assignmentId)
        => db.Exec("UPDATE routine_runs SET assignment_id=$a WHERE id=$i;", ("$a", assignmentId), ("$i", runId));

    public RoutineRunRow? Run(long id) => db.Query($"SELECT {RunCols} FROM routine_runs WHERE id=$i;", MapRun, ("$i", id)).FirstOrDefault();

    public bool HasRunning(long routineId)
        => Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM routine_runs WHERE routine_id=$r AND status='running';", ("$r", routineId)) ?? 0L) > 0;

    public int RunningCount() => Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM routine_runs WHERE status='running';") ?? 0L);

    public IReadOnlyList<RoutineRunRow> Runs(string? bot = null, string? routine = null, int limit = 50)
        => db.Query($"SELECT {RunCols} FROM routine_runs WHERE ($b IS NULL OR bot=$b) AND ($n IS NULL OR routine=$n COLLATE NOCASE) ORDER BY id DESC LIMIT $l;",
            MapRun, ("$b", bot), ("$n", routine), ("$l", limit));

    public void SetFired(long id, DateTimeOffset scheduled, string status)
        => db.Exec("UPDATE routine_state SET last_fire_at=$f, last_status=$s, updated_at=$t WHERE id=$i;",
            ("$f", Key(scheduled)), ("$s", status), ("$t", RosterDb.Now(clock)), ("$i", id));

    public void SetLastStatus(long id, string status)
        => db.Exec("UPDATE routine_state SET last_status=$s, updated_at=$t WHERE id=$i;", ("$s", status), ("$t", RosterDb.Now(clock)), ("$i", id));

    public void SetNext(long id, DateTimeOffset? next)
        => db.Exec("UPDATE routine_state SET next_fire_at=$n WHERE id=$i;", ("$n", next is null ? null : Key(next.Value)), ("$i", id));

    public void SetDone(long id) => db.Exec("UPDATE routine_state SET done=1, next_fire_at=NULL WHERE id=$i;", ("$i", id));

    public void SetPaused(long id, bool paused)
        => db.Exec("UPDATE routine_state SET paused=$p, updated_at=$t WHERE id=$i;", ("$p", paused ? 1 : 0), ("$t", RosterDb.Now(clock)), ("$i", id));

    public void SetRunNow(long id, DateTimeOffset? at)
        => db.Exec("UPDATE routine_state SET run_now_at=$a WHERE id=$i;", ("$a", at is null ? null : Key(at.Value)), ("$i", id));

    public void SetWatch(long id, string? fingerprint, DateTimeOffset? retryAt)
        => db.Exec("UPDATE routine_state SET watch_fingerprint=COALESCE($f, watch_fingerprint), watch_retry_at=$r WHERE id=$i;",
            ("$f", fingerprint), ("$r", retryAt is null ? null : Key(retryAt.Value)), ("$i", id));

    public void SetBudgetNoticeDay(long id, string day) => db.Exec("UPDATE routine_state SET budget_notice_day=$d WHERE id=$i;", ("$d", day), ("$i", id));

    public string? CheckFingerprint(long routineId, int index)
        => db.Scalar("SELECT fingerprint FROM check_state WHERE routine_id=$r AND check_index=$i;", ("$r", routineId), ("$i", index)) as string;

    public void SetCheckFingerprint(long routineId, int index, string fingerprint)
        => db.Exec("""
            INSERT INTO check_state(routine_id, check_index, fingerprint, updated_at) VALUES ($r,$i,$f,$t)
            ON CONFLICT(routine_id, check_index) DO UPDATE SET fingerprint=$f, updated_at=$t;
            """, ("$r", routineId), ("$i", index), ("$f", fingerprint), ("$t", RosterDb.Now(clock)));

    /// <summary>Model calls and tokens a routine used, from the gateway's usage rows.</summary>
    public (int Calls, long Tokens) Usage(string bot, string routine, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var row = db.Query("SELECT COUNT(*), COALESCE(SUM(prompt_tokens + completion_tokens), 0) FROM usage WHERE bot=$b AND routine=$r AND at >= $f AND at <= $t;",
            r => ((int)r.GetInt64(0), r.GetInt64(1)), ("$b", bot), ("$r", routine), ("$f", fromUtc.ToString("O")), ("$t", toUtc.AddSeconds(1).ToString("O"))).First();
        return row;
    }

    public long TokensToday(string bot, string routine, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(clock.UtcNow, zone);
        var start = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset).ToUniversalTime();
        return Convert.ToInt64(db.Scalar("SELECT COALESCE(SUM(prompt_tokens + completion_tokens), 0) FROM usage WHERE bot=$b AND routine=$r AND at >= $s;",
            ("$b", bot), ("$r", routine), ("$s", start.ToString("O"))) ?? 0L);
    }

    /// <summary>
    /// After a crash, runs left "running" by the old process are marked error
    /// (reason interrupted). They are never fired again for that occurrence.
    /// </summary>
    public IReadOnlyList<RoutineRunRow> RecoverInterrupted()
    {
        var rows = db.Query($"SELECT {RunCols} FROM routine_runs WHERE status='running';", MapRun);
        foreach (var r in rows)
            db.Exec("UPDATE routine_runs SET status='error', reason='interrupted by a roster restart', finished_at=$f WHERE id=$i AND status='running';",
                ("$f", RosterDb.Now(clock)), ("$i", r.Id));
        return rows;
    }
}
