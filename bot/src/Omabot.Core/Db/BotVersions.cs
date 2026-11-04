// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Data.Sqlite;
using Omabot.Common;
using Omabot.Team;

namespace Omabot.Db;

public sealed record BotVersion(long Id, string Bot, string SpecJson, string SpecHash, string ChangedBy, string Reason, string At)
{
    public BotSpec Spec() => BotSpec.FromJson(SpecJson);
}

/// <summary>
/// Every stored bot spec, append-only (plan 7.15 "versioned edits with undo").
/// A row is written by team apply and by an approved bot change, with who
/// asked for it. The approver policy reads it: a bot that created or edited
/// another bot never approves for it.
/// </summary>
public sealed class BotVersions(RosterDb db, IClock clock)
{
    private const string Cols = "id, bot, spec_json, spec_hash, changed_by, reason, at";

    private static BotVersion Map(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6));

    public void Record(SqliteConnection c, SqliteTransaction tx, BotSpec spec, string changedBy, string reason)
    {
        using var cmd = RosterDb.Cmd(c, "INSERT INTO bot_versions(bot, spec_json, spec_hash, changed_by, reason, at) VALUES ($b,$j,$h,$c,$r,$t);",
            ("$b", spec.Slug), ("$j", spec.ToJson()), ("$h", spec.Hash()), ("$c", changedBy), ("$r", Redactor.Shared.Redact(reason.Length > 500 ? reason[..500] : reason)), ("$t", RosterDb.Now(clock)));
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    public void Record(BotSpec spec, string changedBy, string reason) => db.InTransaction((c, tx) => Record(c, tx, spec, changedBy, reason));

    public IReadOnlyList<BotVersion> For(string slug, int limit = 50)
        => db.Query($"SELECT {Cols} FROM bot_versions WHERE bot=$b ORDER BY id DESC LIMIT $n;", Map, ("$b", slug), ("$n", limit));

    public BotVersion? Get(long id) => db.Query($"SELECT {Cols} FROM bot_versions WHERE id=$i;", Map, ("$i", id)).FirstOrDefault();

    /// <summary>The version before <paramref name="id"/> for the same bot, or null when it was the first.</summary>
    public BotVersion? Before(BotVersion v)
        => db.Query($"SELECT {Cols} FROM bot_versions WHERE bot=$b AND id < $i ORDER BY id DESC LIMIT 1;", Map, ("$b", v.Bot), ("$i", v.Id)).FirstOrDefault();

    /// <summary>True when <paramref name="actor"/> (for example bot:head-honcho) created or changed the bot, by version rows or audit rows.</summary>
    public bool ChangedBy(string slug, string name, string actor)
    {
        if (Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM bot_versions WHERE bot=$b AND changed_by=$a;", ("$b", slug), ("$a", actor)) ?? 0L) > 0) return true;
        return Convert.ToInt64(db.Scalar(
            "SELECT COUNT(*) FROM audit WHERE actor=$a AND (subject=$s COLLATE NOCASE OR subject=$n COLLATE NOCASE) AND action IN ('bot.create','bot.update','bot.change','bot.undo');",
            ("$a", actor), ("$s", slug), ("$n", name)) ?? 0L) > 0;
    }
}
