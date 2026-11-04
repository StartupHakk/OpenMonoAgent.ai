// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Data.Sqlite;
using Omabot.Common;
using Omabot.Team;

namespace Omabot.Db;

public sealed record BotRow(
    long Id, string Name, string Slug, BotSpec Spec, string SpecHash, bool Enabled, string State, int Port,
    string? ContainerId, string? ImageDigest, string? RenderedHash, string? LastError, string? DisabledReason,
    string CreatedAt, string UpdatedAt, string? LastActiveAt);

/// <summary>Bot rows in roster.db.</summary>
public sealed class BotStore(RosterDb db, IClock clock)
{
    private const string Cols = "id, name, slug, spec_json, spec_hash, enabled, state, port, container_id, image_digest, rendered_hash, last_error, disabled_reason, created_at, updated_at, last_active_at";

    private static BotRow Map(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), BotSpec.FromJson(r.GetString(3)), r.GetString(4), r.GetInt64(5) != 0,
        r.GetString(6), (int)r.GetInt64(7), RosterDb.Str(r, "container_id"), RosterDb.Str(r, "image_digest"),
        RosterDb.Str(r, "rendered_hash"), RosterDb.Str(r, "last_error"), RosterDb.Str(r, "disabled_reason"),
        r.GetString(13), r.GetString(14), RosterDb.Str(r, "last_active_at"));

    public List<BotRow> All() => db.Query($"SELECT {Cols} FROM bots ORDER BY name COLLATE NOCASE;", Map);

    public BotRow? Find(string name)
        => db.Query($"SELECT {Cols} FROM bots WHERE name = $n COLLATE NOCASE OR slug = $s;", Map, ("$n", name), ("$s", Names.Slug(name))).FirstOrDefault();

    public BotRow? FindById(long id) => db.Query($"SELECT {Cols} FROM bots WHERE id = $i;", Map, ("$i", id)).FirstOrDefault();

    public BotRow Get(string name) => Find(name) ?? throw new KeyNotFoundException($"No bot named \"{name}\".");

    public int NextPort(SqliteConnection c, int portBase, int portCount)
    {
        var used = RosterDb.Query(c, "SELECT port FROM bots;", r => (int)r.GetInt64(0)).ToHashSet();
        for (var p = portBase; p < portBase + portCount; p++)
            if (!used.Contains(p)) return p;
        throw new InvalidOperationException("No free ACP port left in the bot port range.");
    }

    public long Insert(SqliteConnection c, SqliteTransaction tx, BotSpec spec, int port)
    {
        var now = RosterDb.Now(clock);
        using var cmd = RosterDb.Cmd(c,
            "INSERT INTO bots(name, slug, spec_json, spec_hash, port, created_at, updated_at) VALUES ($n,$s,$j,$h,$p,$t,$t); SELECT last_insert_rowid();",
            ("$n", spec.Name), ("$s", spec.Slug), ("$j", spec.ToJson()), ("$h", spec.Hash()), ("$p", port), ("$t", now));
        cmd.Transaction = tx;
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public void UpdateSpec(SqliteConnection c, SqliteTransaction tx, long id, BotSpec spec)
    {
        using var cmd = RosterDb.Cmd(c, "UPDATE bots SET name=$n, slug=$s, spec_json=$j, spec_hash=$h, updated_at=$t WHERE id=$i;",
            ("$n", spec.Name), ("$s", spec.Slug), ("$j", spec.ToJson()), ("$h", spec.Hash()), ("$t", RosterDb.Now(clock)), ("$i", id));
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    public void SetState(string slug, string state, string? error = null)
        => db.Exec("UPDATE bots SET state=$st, last_error=COALESCE($e, last_error), updated_at=$t WHERE slug=$s;",
            ("$st", state), ("$e", error), ("$t", RosterDb.Now(clock)), ("$s", slug));

    public void SetContainer(string slug, string? containerId, string? digest)
        => db.Exec("UPDATE bots SET container_id=$c, image_digest=$d WHERE slug=$s;", ("$c", containerId), ("$d", digest), ("$s", slug));

    public void SetRenderedHash(string slug, string hash)
        => db.Exec("UPDATE bots SET rendered_hash=$h WHERE slug=$s;", ("$h", hash), ("$s", slug));

    public void Disable(string slug, string reason)
        => db.Exec("UPDATE bots SET enabled=0, state='disabled', disabled_reason=$r, updated_at=$t WHERE slug=$s;",
            ("$r", reason), ("$t", RosterDb.Now(clock)), ("$s", slug));

    public void Enable(string slug)
        => db.Exec("UPDATE bots SET enabled=1, state='defined', disabled_reason=NULL, updated_at=$t WHERE slug=$s;",
            ("$t", RosterDb.Now(clock)), ("$s", slug));

    public void Touch(string slug)
        => db.Exec("UPDATE bots SET last_active_at=$t WHERE slug=$s;", ("$t", RosterDb.Now(clock)), ("$s", slug));

    public void RecordFailure(long botId, string reason)
        => db.Exec("INSERT INTO bot_failures(bot_id, at, reason) VALUES ($b,$t,$r);", ("$b", botId), ("$t", RosterDb.Now(clock)), ("$r", reason));

    public void SetSession(long botId, string sessionId, string kind = "standing")
        => db.Exec("INSERT INTO sessions(bot_id, session_id, kind, created_at) VALUES ($b,$s,$k,$t) ON CONFLICT(bot_id, kind) DO UPDATE SET session_id=excluded.session_id, created_at=excluded.created_at;",
            ("$b", botId), ("$s", sessionId), ("$k", kind), ("$t", RosterDb.Now(clock)));

    public string? GetSession(long botId, string kind = "standing")
        => db.Scalar("SELECT session_id FROM sessions WHERE bot_id=$b AND kind=$k;", ("$b", botId), ("$k", kind)) as string;

    public void Delete(SqliteConnection c, SqliteTransaction tx, long id)
    {
        foreach (var sql in new[] { "DELETE FROM sessions WHERE bot_id=$i;", "DELETE FROM bot_failures WHERE bot_id=$i;", "DELETE FROM bots WHERE id=$i;" })
        {
            using var cmd = RosterDb.Cmd(c, sql, ("$i", id));
            cmd.Transaction = tx;
            cmd.ExecuteNonQuery();
        }
    }
}
