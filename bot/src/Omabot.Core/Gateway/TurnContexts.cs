// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Omabot.Common;
using Omabot.Db;

namespace Omabot.Gateway;

public sealed record TurnContext(Priority Priority, string? Routine, string? Assignment = null);

/// <summary>
/// One roster-started turn held on a bot. The owner nonce, not the time,
/// identifies the turn, so a lease can only be released or extended by the
/// code that took it, and per-turn shim tokens stop working when it ends.
/// </summary>
public sealed record TurnLease(string Bot, string Owner, Priority Priority, string? Routine);

/// <summary>
/// Who is asking and why: the CLI, scheduler and handoff runtime record the
/// priority of the turn a bot is running, and the gateway reads it. Bots
/// cannot set their own priority; request headers from bots are ignored.
/// The same row is the per-bot turn lock: at most one roster-started turn
/// runs on a bot at a time (<see cref="TryAcquire"/>).
/// </summary>
public sealed class TurnContexts(RosterDb db, IClock clock, Priority fallback)
{
    private readonly Lock _lock = new();
    private Dictionary<string, TurnContext> _cache = new(StringComparer.Ordinal);
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(1);

    /// <summary>Sets the context without checking for a running turn. Tests and single-shot tools only.</summary>
    public void Set(string bot, Priority priority, string? routine, TimeSpan ttl)
    {
        Names.RequireSafeSlug(bot, "bot slug");
        var now = clock.UtcNow;
        db.Exec("""
            INSERT INTO turn_context(bot, priority, routine, set_at, expires_at, owner) VALUES ($b, $p, $r, $s, $e, $o)
            ON CONFLICT(bot) DO UPDATE SET priority=$p, routine=$r, set_at=$s, expires_at=$e, owner=$o;
            """, ("$b", bot), ("$p", Priorities.Name(priority)), ("$r", routine), ("$s", now.ToString("O")), ("$e", (now + ttl).ToString("O")), ("$o", NewOwner()));
        Invalidate();
    }

    public void Clear(string bot)
    {
        db.Exec("DELETE FROM turn_context WHERE bot=$b;", ("$b", bot));
        Invalidate();
    }

    /// <summary>
    /// Takes the bot's turn when no unexpired turn holds it. Returns null when
    /// the bot is busy. Atomic across processes (one immediate transaction).
    /// </summary>
    public TurnLease? TryAcquire(string bot, Priority priority, string? routine, TimeSpan ttl, string? assignment = null)
    {
        Names.RequireSafeSlug(bot, "bot slug");
        var owner = NewOwner();
        var now = clock.UtcNow;
        var taken = db.InTransaction((c, tx) =>
        {
            using var sel = RosterDb.Cmd(c, "SELECT expires_at FROM turn_context WHERE bot=$b;", ("$b", bot));
            sel.Transaction = tx;
            if (sel.ExecuteScalar() is string exp && Parse(exp) > now) return false;
            using var up = RosterDb.Cmd(c, """
                INSERT INTO turn_context(bot, priority, routine, set_at, expires_at, owner, assignment) VALUES ($b, $p, $r, $s, $e, $o, $a)
                ON CONFLICT(bot) DO UPDATE SET priority=$p, routine=$r, set_at=$s, expires_at=$e, owner=$o, assignment=$a;
                """, ("$b", bot), ("$p", Priorities.Name(priority)), ("$r", routine), ("$s", now.ToString("O")), ("$e", (now + ttl).ToString("O")), ("$o", owner), ("$a", assignment));
            up.Transaction = tx;
            up.ExecuteNonQuery();
            return true;
        });
        Invalidate();
        return taken ? new TurnLease(bot, owner, priority, routine) : null;
    }

    /// <summary>Records which assignment the held turn works on (the shim and gateway read it).</summary>
    public void SetAssignment(TurnLease lease, string? assignment)
    {
        db.Exec("UPDATE turn_context SET assignment=$a WHERE bot=$b AND owner=$o;", ("$a", assignment), ("$b", lease.Bot), ("$o", lease.Owner));
        Invalidate();
    }

    /// <summary>Waits up to <paramref name="wait"/> for the bot to be free.</summary>
    public async Task<TurnLease?> AcquireAsync(string bot, Priority priority, string? routine, TimeSpan ttl, TimeSpan wait, TimeSpan poll, CancellationToken ct)
    {
        var deadline = clock.UtcNow + wait;
        while (true)
        {
            var lease = TryAcquire(bot, priority, routine, ttl);
            if (lease is not null || clock.UtcNow >= deadline) return lease;
            await Task.Delay(poll, ct).ConfigureAwait(false);
        }
    }

    /// <summary>True while the lease is still the bot's current turn.</summary>
    public bool Holds(TurnLease lease)
    {
        var row = db.Query("SELECT owner, expires_at FROM turn_context WHERE bot=$b;", r => (Owner: RosterDb.Str(r, "owner"), Exp: r.GetString(1)), ("$b", lease.Bot)).FirstOrDefault();
        return row.Owner == lease.Owner && Parse(row.Exp) > clock.UtcNow;
    }

    public void Extend(TurnLease lease, TimeSpan ttl)
    {
        db.Exec("UPDATE turn_context SET expires_at=$e WHERE bot=$b AND owner=$o;",
            ("$e", (clock.UtcNow + ttl).ToString("O")), ("$b", lease.Bot), ("$o", lease.Owner));
        Invalidate();
    }

    /// <summary>Ends the turn. A lease that was already replaced is left alone.</summary>
    public void Release(TurnLease lease)
    {
        db.Exec("DELETE FROM turn_context WHERE bot=$b AND owner=$o;", ("$b", lease.Bot), ("$o", lease.Owner));
        Invalidate();
    }

    /// <summary>The owner of the bot's current unexpired turn, or null.</summary>
    public string? OwnerOf(string bot)
    {
        var row = db.Query("SELECT owner, expires_at FROM turn_context WHERE bot=$b;", r => (Owner: RosterDb.Str(r, "owner"), Exp: r.GetString(1)), ("$b", bot)).FirstOrDefault();
        return row.Exp is not null && Parse(row.Exp) > clock.UtcNow ? row.Owner : null;
    }

    /// <summary>True when a roster-started turn holds the bot.</summary>
    public bool Busy(string bot)
        => db.Scalar("SELECT expires_at FROM turn_context WHERE bot=$b;", ("$b", bot)) is string exp && Parse(exp) > clock.UtcNow;

    public TurnContext For(string bot)
    {
        lock (_lock)
        {
            var now = clock.UtcNow;
            if (now - _loadedAt > CacheFor)
            {
                _cache = db.Query("SELECT bot, priority, routine, expires_at, assignment FROM turn_context;",
                        r => (Bot: r.GetString(0), P: r.GetString(1), R: r.IsDBNull(2) ? null : r.GetString(2), E: Parse(r.GetString(3)), A: r.IsDBNull(4) ? null : r.GetString(4)))
                    .Where(x => x.E > now)
                    .ToDictionary(x => x.Bot, x => new TurnContext(Priorities.Parse(x.P), x.R, x.A), StringComparer.Ordinal);
                _loadedAt = now;
            }
            return _cache.TryGetValue(bot, out var c) ? c : new TurnContext(fallback, null);
        }
    }

    private void Invalidate()
    {
        lock (_lock) _loadedAt = DateTimeOffset.MinValue;
    }

    private static string NewOwner() => "turn_" + Guid.NewGuid().ToString("N");

    private static DateTimeOffset Parse(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture);
}
