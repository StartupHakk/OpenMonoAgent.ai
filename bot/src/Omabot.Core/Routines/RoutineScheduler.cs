// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Omabot.Common;
using Omabot.Config;
using Omabot.Db;
using Omabot.Gateway;
using Omabot.Memory;

namespace Omabot.Routines;

/// <summary>
/// The routine scheduler (plan 7.4), run by `oma-bot serve` on every tick.
/// It reads the clock in UTC and converts per routine zone, runs checks
/// before any model call, logs one line per run, notifies only on errors,
/// and never fires one occurrence twice (unique run key). Runs that need the
/// model take the bot's turn lease and stay inside the routine share of
/// inference slots.
/// </summary>
public sealed class RoutineScheduler
{
    private enum FireOutcome { Done, Deferred }

    private readonly RosterDb _db;
    private readonly RosterConfig _config;
    private readonly RosterPaths _paths;
    private readonly IClock _clock;
    private readonly AuditLog _audit;
    private readonly Notices _notices;
    private readonly IRoutineRunner _runner;
    private readonly CheckEnvironment _checks;
    private readonly Func<int> _slots;
    private readonly ConcurrentDictionary<long, Task> _inflight = new();

    public RoutineScheduler(RosterDb db, RosterConfig config, RosterPaths paths, IClock clock, AuditLog audit, Notices notices,
        IRoutineRunner runner, CheckEnvironment checks, Func<int> slots)
    {
        _db = db;
        _config = config;
        _paths = paths;
        _clock = clock;
        _audit = audit;
        _notices = notices;
        _runner = runner;
        _checks = checks;
        _slots = slots;
        Store = new RoutineStore(db, clock);
        Turns = new TurnContexts(db, clock, Priority.Assignment);
    }

    public RoutineStore Store { get; }
    public TurnContexts Turns { get; }

    /// <summary>Waits for model runs started by earlier ticks (tests and shutdown).</summary>
    public Task WhenIdleAsync() => Task.WhenAll(_inflight.Values.ToArray());

    public int InFlight => _inflight.Count;

    /// <summary>At service start: runs a crash left "running" become errors, once, and are never fired again.</summary>
    public IReadOnlyList<string> Recover()
    {
        var lines = new List<string>();
        foreach (var run in Store.RecoverInterrupted())
        {
            // The dead process's turn lease would block the bot until it expired; release exactly that lease.
            if (run.Owner is not null) _db.Exec("DELETE FROM turn_context WHERE owner=$o;", ("$o", run.Owner));
            var state = Store.Get(run.RoutineId);
            var zone = ZoneOf(run.Bot, run.Routine) ?? "UTC";
            var notified = _notices.Add("error", run.Bot, $"Routine {run.Bot}/{run.Routine} was interrupted by a roster restart and was not run again.", "routine_run:" + run.Id);
            if (notified) _db.Exec("UPDATE routine_runs SET notified=1 WHERE id=$i;", ("$i", run.Id));
            var line = RoutineLog.Line(run.StartedAt ?? _clock.UtcNow, zone, run.Bot, run.Routine, RoutineStore.Status.Error, "interrupted", run.Checks, 0, 0, 0, notified);
            RoutineLog.Append(_paths, run.StartedAt ?? _clock.UtcNow, zone, line);
            if (state is not null) Store.SetLastStatus(state.Id, RoutineStore.Status.Error);
            lines.Add(line);
        }
        return lines;
    }

    private string? ZoneOf(string slug, string routine)
        => new BotStore(_db, _clock).Find(slug)?.Spec.Routines.FirstOrDefault(r => Names.SameName(r.Name, routine))?.Timezone;

    public async Task<IReadOnlyList<string>> TickAsync(CancellationToken ct)
    {
        var lines = new List<string>();
        var bots = new BotStore(_db, _clock).All();
        foreach (var r in Store.Sync(bots))
        {
            if (!r.Bot.Enabled || !r.Spec.Enabled || r.State.Paused || r.State.Orphaned)
            {
                SetNext(r.State, null);
                continue;
            }
            try
            {
                await TickOneAsync(r, lines, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or SqliteException or ArgumentException or TimeZoneNotFoundException)
            {
                lines.Add($"{r.Bot.Name}/{r.Spec.Name}: scheduler problem: {Redactor.Shared.Redact(ex.Message)}");
            }
        }
        return lines;
    }

    private static DateTimeOffset Second(DateTimeOffset t) => new(t.UtcTicks - t.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);

    /// <summary>A fixed 0 to max jitter per routine, so routines do not all hit the server at :00 (plan 7.4).</summary>
    public TimeSpan JitterFor(string bot, string routine)
    {
        var max = Math.Max(0, _config.Routines.MaxJitterSeconds);
        if (max == 0) return TimeSpan.Zero;
        var h = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(bot + "/" + routine.ToLowerInvariant()));
        return TimeSpan.FromSeconds(BitConverter.ToUInt32(h, 0) % (uint)(max + 1));
    }

    private async Task TickOneAsync(ScheduledRoutine r, List<string> lines, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var st = r.State;
        if (st.RunNowAt is { } rn && rn <= now)
        {
            if (await FireAsync(r, Second(now), "run_now", null, lines, ct).ConfigureAwait(false) == FireOutcome.Done) Store.SetRunNow(st.Id, null);
        }
        var kind = RoutineRules.Kind(r.Spec);
        if (kind == "watch")
        {
            await WatchAsync(r, lines, ct).ConfigureAwait(false);
            return;
        }
        if (st.Done) return;

        var from = st.LastFire ?? st.Anchor;
        var windowStart = now - RoutineRules.MaxScanWindow;
        if (from < windowStart)
        {
            Record(r, windowStart, kind, RoutineStore.Status.Missed, "the roster was down for over 30 days; older occurrences were not counted", 0, 0, lines, setFired: false);
            from = windowStart;
        }
        // An occurrence is due once its jitter has passed; later ones are not counted yet, so a slow tick never starves a routine.
        var jitter = JitterFor(r.Bot.Slug, r.Spec.Name);
        DateTimeOffset? latest = null;
        var older = new List<DateTimeOffset>();
        long olderCount = 0;
        foreach (var t in RoutineRules.Between(r.Spec, from, now - jitter, st.Anchor))
        {
            if (latest is { } prev)
            {
                olderCount++;
                if (older.Count < _config.Routines.MaxMissedRows) older.Add(prev);
            }
            latest = t;
        }
        if (latest is null)
        {
            SetNext(st, RoutineRules.Next(r.Spec, from > now - jitter ? from : now - jitter, st.Anchor));
            return;
        }
        foreach (var t in older) Record(r, t, kind, RoutineStore.Status.Missed, "a later occurrence ran in its place", 0, 0, lines, setFired: false);
        if (olderCount > older.Count)
            Record(r, older[^1].AddSeconds(1), kind, RoutineStore.Status.Missed, $"and {olderCount - older.Count} more occurrences missed", 0, 0, lines, setFired: false);
        var catchUp = TimeSpan.FromHours(_config.Routines.CatchUpHours);
        if (now - latest.Value > catchUp)
        {
            Record(r, latest.Value, kind, RoutineStore.Status.Missed, $"older than the {_config.Routines.CatchUpHours} hour catch-up window", 0, 0, lines, setFired: true);
        }
        else
        {
            var trigger = now - latest.Value > jitter + TimeSpan.FromMinutes(2) ? "catch_up" : kind;
            if (await FireAsync(r, latest.Value, trigger, null, lines, ct).ConfigureAwait(false) == FireOutcome.Deferred)
            {
                SetNext(st, latest.Value);
                return;
            }
        }
        if (kind == "one_time") Store.SetDone(st.Id);
        else SetNext(st, RoutineRules.Next(r.Spec, latest.Value, latest.Value));
    }

    private void SetNext(RoutineStateRow st, DateTimeOffset? next)
    {
        if (st.NextFire != next) Store.SetNext(st.Id, next);
    }

    private async Task WatchAsync(ScheduledRoutine r, List<string> lines, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var st = r.State;
        if (st.WatchRetryAt is { } retry && now < retry) return;
        if (!WatchTarget.TryParse(r.Spec.Watch ?? "", out var target, out _)) return;
        var def = target.Kind switch
        {
            "assignments" => new Team.CheckDef { Kind = "assignments_waiting" },
            "doctor" => new Team.CheckDef { Kind = "doctor" },
            "command" => new Team.CheckDef { Kind = "command", Args = new(StringComparer.Ordinal) { ["run"] = target.Value } },
            _ => new Team.CheckDef { Kind = "file_changed", Args = new(StringComparer.Ordinal) { ["path"] = target.Value } },
        };
        var ws = MemoryStores.WorkspaceOf(_paths, r.Bot.Spec);
        var o = await Checks.RunAsync(def, r.Bot.Spec, ws, st.WatchFingerprint, _checks, ct).ConfigureAwait(false);
        if (o.Error is not null)
        {
            if (_notices.Add("warn", r.Bot.Name, $"Watch for routine {r.Bot.Name}/{r.Spec.Name} cannot read its target: {o.Error}", $"watch_error:{st.Id}:{SecureFile.Sha256Hex(o.Error)[..8]}"))
                lines.Add($"{r.Bot.Name}/{r.Spec.Name}: watch problem: {o.Error}");
            return;
        }
        var changed = target.Kind == "command" ? o.Changed && o.Fingerprint != st.WatchFingerprint : o.Changed;
        if (st.WatchFingerprint is null && target.Kind is "path") changed = false;
        if (!changed)
        {
            Store.SetWatch(st.Id, o.Fingerprint, null);
            return;
        }
        if (await FireAsync(r, Second(now), "watch", o.Output, lines, ct).ConfigureAwait(false) == FireOutcome.Deferred)
        {
            // A busy bot is not a failure: keep the old fingerprint and look again in a minute.
            Store.SetWatch(st.Id, null, now + TimeSpan.FromSeconds(_config.Routines.WatchRetrySeconds));
            return;
        }
        Store.SetWatch(st.Id, o.Fingerprint, null);
    }

    private async Task<FireOutcome> FireAsync(ScheduledRoutine r, DateTimeOffset scheduled, string trigger, string? watchOutput, List<string> lines, CancellationToken ct)
    {
        var (bot, spec, st) = (r.Bot, r.Spec, r.State);
        var sw = Stopwatch.StartNew();
        if (Store.HasRunning(st.Id))
        {
            Record(r, scheduled, trigger, RoutineStore.Status.SkippedOverlap, "the previous run is still going", 0, 0, lines, setFired: true);
            return FireOutcome.Done;
        }
        var zone = TimeZoneInfo.FindSystemTimeZoneById(spec.Timezone);
        if (spec.DailyTokenBudget > 0 && Store.TokensToday(bot.Slug, spec.Name, zone) >= spec.DailyTokenBudget)
        {
            var day = TimeZoneInfo.ConvertTime(_clock.UtcNow, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var first = st.BudgetNoticeDay != day;
            if (first)
            {
                Store.SetBudgetNoticeDay(st.Id, day);
                _notices.Add("error", bot.Name, $"Routine {bot.Name}/{spec.Name} used its daily token budget ({spec.DailyTokenBudget.ToString(CultureInfo.InvariantCulture)}); runs are skipped until tomorrow.", $"routine_budget:{bot.Slug}/{spec.Name}:{day}");
            }
            Record(r, scheduled, trigger, RoutineStore.Status.SkippedBudget, "daily token budget used", 0, 0, lines, setFired: true, notifiedOverride: first);
            return FireOutcome.Done;
        }

        var ws = MemoryStores.WorkspaceOf(_paths, bot.Spec);
        var outcomes = new List<CheckOutcome>();
        for (var i = 0; i < spec.Checks.Count; i++)
            outcomes.Add(await Checks.RunAsync(spec.Checks[i], bot.Spec, ws, Store.CheckFingerprint(st.Id, i), _checks, ct).ConfigureAwait(false));
        if (outcomes.Select((o, i) => (o, i)).FirstOrDefault(x => x.o.Error is not null) is { o: not null } bad)
        {
            Record(r, scheduled, trigger, RoutineStore.Status.Error, $"check {spec.Checks[bad.i].Kind}: {bad.o.Error}", spec.Checks.Count, sw.ElapsedMilliseconds, lines, setFired: true);
            return FireOutcome.Done;
        }
        var changed = spec.Checks.Count == 0 || outcomes.Any(o => o.Changed);
        if (!changed)
        {
            SaveFingerprints(st.Id, outcomes);
            Record(r, scheduled, trigger, RoutineStore.Status.Quiet, null, spec.Checks.Count, sw.ElapsedMilliseconds, lines, setFired: true);
            return FireOutcome.Done;
        }

        var cap = Math.Max(1, (int)Math.Floor(Math.Max(1, _slots()) * _config.Routines.SlotShare));
        if (Store.RunningCount() >= cap) return FireOutcome.Deferred;
        var lease = Turns.TryAcquire(bot.Slug, Priority.Routine, spec.Name, TimeSpan.FromMinutes(_config.Routines.MaxRunMinutes + 2));
        if (lease is null) return FireOutcome.Deferred;
        var runId = Store.TryStartRun(st.Id, bot.Slug, spec.Name, scheduled, trigger, RoutineStore.Status.Running, lease.Owner);
        if (runId is null)
        {
            Turns.Release(lease);
            return FireOutcome.Done;
        }
        SaveFingerprints(st.Id, outcomes);
        Store.SetFired(st.Id, scheduled, RoutineStore.Status.Running);
        var input = string.Join("\n", new[] { watchOutput }.Concat(outcomes.Where(o => o.Changed).Select(o => o.Output)).Where(s => !string.IsNullOrWhiteSpace(s)));
        input = Checks.Cap(input, _config.Routines.OutputCapBytes);
        var started = _clock.UtcNow;
        var checks = spec.Checks.Count;
        var id = runId.Value;
        var task = Task.Run(async () =>
        {
            RoutineRunResult result;
            try
            {
                result = await _runner.RunAsync(new RoutineRunRequest(id, bot, spec, trigger, scheduled, input, lease), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = new RoutineRunResult(RoutineStore.Status.Error, "stopped", "");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = new RoutineRunResult(RoutineStore.Status.Error, "runner_error: " + Redactor.Shared.Redact(ex.Message), "");
            }
            finally
            {
                Turns.Release(lease);
            }
            Complete(r, id, started, result, checks);
        }, CancellationToken.None);
        _inflight[id] = task;
        _ = task.ContinueWith(_ => _inflight.TryRemove(id, out var _), TaskScheduler.Default);
        lines.Add($"{bot.Name}/{spec.Name}: run {id} started ({trigger})");
        return FireOutcome.Done;
    }

    private void SaveFingerprints(long routineId, List<CheckOutcome> outcomes)
    {
        for (var i = 0; i < outcomes.Count; i++)
            if (outcomes[i].Fingerprint.Length > 0) Store.SetCheckFingerprint(routineId, i, outcomes[i].Fingerprint);
    }

    private void Complete(ScheduledRoutine r, long runId, DateTimeOffset started, RoutineRunResult result, int checks)
    {
        var end = _clock.UtcNow;
        var (calls, tokens) = Store.Usage(r.Bot.Slug, r.Spec.Name, started, end);
        var ms = (long)(end - started).TotalMilliseconds;
        // A restart may already have closed this run as interrupted; then it stays that way and is not reported twice.
        if (!Store.FinishRun(runId, result.Status, result.Reason, checks, calls, tokens, ms, notified: false)) return;
        var notify = RoutineStore.Status.Notifying.Contains(result.Status) && r.Spec.Notify == "errors_only";
        var notified = notify && _notices.Add("error", r.Bot.Name,
            $"Routine {r.Bot.Name}/{r.Spec.Name} ended {result.Status}{(result.Reason is null ? "" : ": " + result.Reason)}. See {RoutineLog.FileFor(_paths, started, r.Spec.Timezone)}.",
            "routine_run:" + runId);
        if (notified) _db.Exec("UPDATE routine_runs SET notified=1 WHERE id=$i;", ("$i", runId));
        Store.SetLastStatus(r.State.Id, result.Status);
        RoutineLog.Append(_paths, started, r.Spec.Timezone, RoutineLog.Line(started, r.Spec.Timezone, r.Bot.Slug, r.Spec.Name, result.Status, result.Reason, checks, calls, tokens, ms, notified));
        _audit.Write("roster", "routine.run", $"{r.Bot.Name}/{r.Spec.Name}", $"run {runId} {result.Status}{(result.Reason is null ? "" : " " + result.Reason)} model_calls {calls}");
    }

    /// <summary>Records an outcome that needs no model call (quiet, skipped, missed, check error). One line, one row.</summary>
    private void Record(ScheduledRoutine r, DateTimeOffset scheduled, string trigger, string status, string? reason, int checks, long ms, List<string> lines, bool setFired, bool? notifiedOverride = null)
    {
        var id = Store.TryStartRun(r.State.Id, r.Bot.Slug, r.Spec.Name, scheduled, trigger, status, null, reason);
        if (id is null) return;
        var notify = notifiedOverride ?? (RoutineStore.Status.Notifying.Contains(status) && r.Spec.Notify == "errors_only");
        var notified = notifiedOverride ?? (notify && _notices.Add("error", r.Bot.Name,
            $"Routine {r.Bot.Name}/{r.Spec.Name} {status}{(reason is null ? "" : ": " + reason)}.", "routine_run:" + id));
        Store.FinishRun(id.Value, status, reason, checks, 0, 0, ms, notified, onlyIfRunning: false);
        if (setFired) Store.SetFired(r.State.Id, scheduled, status);
        var now = _clock.UtcNow;
        var line = RoutineLog.Line(now, r.Spec.Timezone, r.Bot.Slug, r.Spec.Name, status, reason, checks, 0, 0, ms, notified);
        RoutineLog.Append(_paths, now, r.Spec.Timezone, line);
        if (status != RoutineStore.Status.Quiet) lines.Add(line);
    }
}
