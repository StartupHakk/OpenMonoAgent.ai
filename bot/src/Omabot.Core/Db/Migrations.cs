// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Data.Sqlite;

namespace Omabot.Db;

/// <summary>
/// Ordered schema migrations. Each phase appends. A migration never edits an
/// earlier one. Append-only tables are enforced with triggers.
/// </summary>
public static class Migrations
{
    public static readonly (int Version, string Name, string Sql)[] All =
    [
        (1, "phase1 bots sessions audit notices", """
            CREATE TABLE bots (
              id INTEGER PRIMARY KEY,
              name TEXT NOT NULL UNIQUE COLLATE NOCASE,
              slug TEXT NOT NULL UNIQUE,
              spec_json TEXT NOT NULL,
              spec_hash TEXT NOT NULL,
              enabled INTEGER NOT NULL DEFAULT 1,
              state TEXT NOT NULL DEFAULT 'defined',
              port INTEGER NOT NULL UNIQUE,
              container_id TEXT,
              image_digest TEXT,
              rendered_hash TEXT,
              last_error TEXT,
              disabled_reason TEXT,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              last_active_at TEXT
            );
            CREATE TABLE bot_failures (
              id INTEGER PRIMARY KEY,
              bot_id INTEGER NOT NULL REFERENCES bots(id),
              at TEXT NOT NULL,
              reason TEXT NOT NULL
            );
            CREATE TABLE sessions (
              id INTEGER PRIMARY KEY,
              bot_id INTEGER NOT NULL REFERENCES bots(id),
              session_id TEXT NOT NULL,
              kind TEXT NOT NULL DEFAULT 'standing',
              created_at TEXT NOT NULL,
              UNIQUE(bot_id, kind)
            );
            CREATE TABLE audit (
              id INTEGER PRIMARY KEY,
              at TEXT NOT NULL,
              actor TEXT NOT NULL,
              action TEXT NOT NULL,
              subject TEXT NOT NULL,
              detail TEXT NOT NULL,
              prev_hash TEXT NOT NULL,
              hash TEXT NOT NULL
            );
            CREATE TRIGGER audit_no_update BEFORE UPDATE ON audit BEGIN SELECT RAISE(ABORT, 'audit is append-only'); END;
            CREATE TRIGGER audit_no_delete BEFORE DELETE ON audit BEGIN SELECT RAISE(ABORT, 'audit is append-only'); END;
            CREATE TABLE notices (
              id INTEGER PRIMARY KEY,
              at TEXT NOT NULL,
              level TEXT NOT NULL,
              bot TEXT,
              text TEXT NOT NULL,
              dedupe_key TEXT,
              seen INTEGER NOT NULL DEFAULT 0
            );
            CREATE UNIQUE INDEX notices_dedupe ON notices(dedupe_key) WHERE dedupe_key IS NOT NULL AND seen = 0;
            CREATE TABLE team_items (
              kind TEXT NOT NULL,
              name TEXT NOT NULL COLLATE NOCASE,
              json TEXT NOT NULL,
              hash TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              PRIMARY KEY(kind, name)
            );
            CREATE TABLE images (
              id INTEGER PRIMARY KEY,
              digest TEXT NOT NULL UNIQUE,
              accepted_at TEXT NOT NULL,
              accepted_by TEXT NOT NULL
            );
            """),
        (2, "phase2 usage and turn context", """
            CREATE TABLE usage (
              id INTEGER PRIMARY KEY,
              at TEXT NOT NULL,
              bot TEXT NOT NULL,
              routine TEXT,
              priority TEXT NOT NULL,
              backend TEXT NOT NULL,
              model TEXT NOT NULL,
              prompt_tokens INTEGER NOT NULL DEFAULT 0,
              completion_tokens INTEGER NOT NULL DEFAULT 0,
              estimated INTEGER NOT NULL DEFAULT 0,
              queue_ms INTEGER NOT NULL DEFAULT 0,
              total_ms INTEGER NOT NULL DEFAULT 0,
              status INTEGER NOT NULL
            );
            CREATE INDEX usage_bot_at ON usage(bot, at);
            CREATE TABLE turn_context (
              bot TEXT PRIMARY KEY,
              priority TEXT NOT NULL,
              routine TEXT,
              set_at TEXT NOT NULL,
              expires_at TEXT NOT NULL
            );
            """),
        (3, "phase3 memory journal", """
            CREATE TABLE memory_journal (
              id INTEGER PRIMARY KEY,
              at TEXT NOT NULL,
              bot TEXT NOT NULL,
              actor TEXT NOT NULL,
              file TEXT NOT NULL,
              before_hash TEXT NOT NULL,
              after_hash TEXT NOT NULL,
              diff TEXT NOT NULL,
              reason TEXT NOT NULL,
              before_text TEXT,
              after_text TEXT,
              undoable INTEGER NOT NULL DEFAULT 1
            );
            CREATE INDEX memory_journal_bot ON memory_journal(bot, id);
            CREATE TABLE memory_forgotten (
              id INTEGER PRIMARY KEY,
              bot TEXT NOT NULL,
              fact_norm TEXT NOT NULL,
              at TEXT NOT NULL
            );
            CREATE TABLE memory_state (
              bot TEXT PRIMARY KEY,
              last_turn_at TEXT,
              turns_since INTEGER NOT NULL DEFAULT 0,
              last_capture_at TEXT,
              last_tidy_day TEXT
            );
            """),
    ];

    public static void Apply(RosterDb db)
    {
        using var c = db.Open();
        RosterDb.Exec(c, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL);");
        var current = Convert.ToInt32(RosterDb.Scalar(c, "SELECT COALESCE(MAX(version), 0) FROM schema_version;") ?? 0L);
        foreach (var (version, name, sql) in All.OrderBy(m => m.Version))
        {
            if (version <= current) continue;
            using var tx = c.BeginTransaction();
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
            using (var cmd = c.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO schema_version(version, name, applied_at) VALUES ($v, $n, $t);";
                cmd.Parameters.AddWithValue("$v", version);
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public static int CurrentVersion(SqliteConnection c)
        => Convert.ToInt32(RosterDb.Scalar(c, "SELECT COALESCE(MAX(version), 0) FROM schema_version;") ?? 0L);
}
