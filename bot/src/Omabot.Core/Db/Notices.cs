// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Common;

namespace Omabot.Db;

/// <summary>Notices for Spencer (builder choice B3). One unseen notice per dedupe key, so no spam.</summary>
public sealed class Notices(RosterDb db, IClock clock, Redactor redactor)
{
    public bool Add(string level, string? bot, string text, string? dedupeKey = null)
    {
        var n = db.Exec("INSERT OR IGNORE INTO notices(at, level, bot, text, dedupe_key) VALUES ($a,$l,$b,$t,$k);",
            ("$a", RosterDb.Now(clock)), ("$l", level), ("$b", bot), ("$t", redactor.Redact(text)), ("$k", dedupeKey));
        return n > 0;
    }

    public List<(long Id, string At, string Level, string? Bot, string Text)> Unseen()
        => db.Query("SELECT id, at, level, bot, text FROM notices WHERE seen = 0 ORDER BY id;",
            r => (r.GetInt64(0), r.GetString(1), r.GetString(2), RosterDb.Str(r, "bot"), r.GetString(4)));

    public void MarkSeen(long id) => db.Exec("UPDATE notices SET seen = 1 WHERE id = $i;", ("$i", id));

    public int Count(string? dedupePrefix = null)
        => Convert.ToInt32(dedupePrefix is null
            ? db.Scalar("SELECT COUNT(*) FROM notices;")
            : db.Scalar("SELECT COUNT(*) FROM notices WHERE dedupe_key LIKE $p;", ("$p", dedupePrefix + "%")));
}
