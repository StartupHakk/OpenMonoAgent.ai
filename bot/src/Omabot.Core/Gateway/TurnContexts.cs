// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Common;
using Omabot.Db;

namespace Omabot.Gateway;

public sealed record TurnContext(Priority Priority, string? Routine);

/// <summary>
/// Who is asking and why: the CLI, scheduler and handoff runtime record the
/// priority of the turn a bot is running, and the gateway reads it. Bots
/// cannot set their own priority; request headers from bots are ignored.
/// </summary>
public sealed class TurnContexts(RosterDb db, IClock clock, Priority fallback)
{
    private readonly Lock _lock = new();
    private Dictionary<string, TurnContext> _cache = new(StringComparer.Ordinal);
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;

    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(1);

    public void Set(string bot, Priority priority, string? routine, TimeSpan ttl)
    {
        Names.RequireSafeSlug(bot, "bot slug");
        var now = clock.UtcNow;
        db.Exec("""
            INSERT INTO turn_context(bot, priority, routine, set_at, expires_at) VALUES ($b, $p, $r, $s, $e)
            ON CONFLICT(bot) DO UPDATE SET priority=$p, routine=$r, set_at=$s, expires_at=$e;
            """, ("$b", bot), ("$p", Priorities.Name(priority)), ("$r", routine), ("$s", now.ToString("O")), ("$e", (now + ttl).ToString("O")));
        lock (_lock) _loadedAt = DateTimeOffset.MinValue;
    }

    public void Clear(string bot)
    {
        db.Exec("DELETE FROM turn_context WHERE bot=$b;", ("$b", bot));
        lock (_lock) _loadedAt = DateTimeOffset.MinValue;
    }

    public TurnContext For(string bot)
    {
        lock (_lock)
        {
            var now = clock.UtcNow;
            if (now - _loadedAt > CacheFor)
            {
                _cache = db.Query("SELECT bot, priority, routine, expires_at FROM turn_context;", r => (Bot: r.GetString(0), P: r.GetString(1), R: r.IsDBNull(2) ? null : r.GetString(2), E: DateTimeOffset.Parse(r.GetString(3), System.Globalization.CultureInfo.InvariantCulture)))
                    .Where(x => x.E > now)
                    .ToDictionary(x => x.Bot, x => new TurnContext(Priorities.Parse(x.P), x.R), StringComparer.Ordinal);
                _loadedAt = now;
            }
            return _cache.TryGetValue(bot, out var c) ? c : new TurnContext(fallback, null);
        }
    }
}
