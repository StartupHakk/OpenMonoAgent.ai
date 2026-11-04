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
        (4, "phase4 routines, runs, approvals and turn owner", """
            ALTER TABLE turn_context ADD COLUMN owner TEXT;
            CREATE TABLE routine_state (
              id INTEGER PRIMARY KEY,
              bot TEXT NOT NULL,
              name TEXT NOT NULL COLLATE NOCASE,
              spec_hash TEXT NOT NULL,
              paused INTEGER NOT NULL DEFAULT 0,
              orphaned INTEGER NOT NULL DEFAULT 0,
              done INTEGER NOT NULL DEFAULT 0,
              anchor_at TEXT NOT NULL,
              next_fire_at TEXT,
              last_fire_at TEXT,
              last_status TEXT,
              watch_fingerprint TEXT,
              watch_retry_at TEXT,
              run_now_at TEXT,
              budget_notice_day TEXT,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              UNIQUE(bot, name)
            );
            CREATE TABLE routine_runs (
              id INTEGER PRIMARY KEY,
              routine_id INTEGER NOT NULL REFERENCES routine_state(id),
              bot TEXT NOT NULL,
              routine TEXT NOT NULL,
              scheduled_for TEXT NOT NULL,
              trigger TEXT NOT NULL,
              status TEXT NOT NULL,
              reason TEXT,
              started_at TEXT,
              finished_at TEXT,
              checks INTEGER NOT NULL DEFAULT 0,
              model_calls INTEGER NOT NULL DEFAULT 0,
              tokens INTEGER NOT NULL DEFAULT 0,
              ms INTEGER NOT NULL DEFAULT 0,
              notified INTEGER NOT NULL DEFAULT 0,
              owner TEXT,
              assignment_id TEXT,
              UNIQUE(routine_id, scheduled_for)
            );
            CREATE INDEX routine_runs_bot ON routine_runs(bot, routine, started_at);
            CREATE INDEX routine_runs_status ON routine_runs(status);
            CREATE TABLE check_state (
              routine_id INTEGER NOT NULL,
              check_index INTEGER NOT NULL,
              fingerprint TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              PRIMARY KEY(routine_id, check_index)
            );
            CREATE TABLE approvals (
              id TEXT PRIMARY KEY,
              kind TEXT NOT NULL,
              status TEXT NOT NULL DEFAULT 'pending',
              user_id TEXT,
              bot TEXT NOT NULL,
              session_id TEXT,
              pause_id TEXT,
              assignment_id TEXT,
              root_id TEXT,
              origin TEXT NOT NULL DEFAULT 'person',
              summary TEXT NOT NULL,
              payload TEXT NOT NULL,
              action_hash TEXT NOT NULL,
              requester_kind TEXT NOT NULL,
              requester TEXT NOT NULL,
              approver_kind TEXT,
              approver TEXT,
              decision TEXT,
              reason TEXT,
              created_at TEXT NOT NULL,
              expires_at TEXT NOT NULL,
              decided_at TEXT,
              delivered_at TEXT,
              reminded_at TEXT
            );
            CREATE INDEX approvals_status ON approvals(status, created_at);
            CREATE UNIQUE INDEX approvals_pause ON approvals(bot, session_id, pause_id) WHERE pause_id IS NOT NULL;
            CREATE TABLE approval_events (
              id INTEGER PRIMARY KEY,
              approval_id TEXT NOT NULL,
              at TEXT NOT NULL,
              actor TEXT NOT NULL,
              event TEXT NOT NULL,
              detail TEXT NOT NULL
            );
            CREATE TRIGGER approval_events_no_update BEFORE UPDATE ON approval_events BEGIN SELECT RAISE(ABORT, 'approval_events is append-only'); END;
            CREATE TRIGGER approval_events_no_delete BEFORE DELETE ON approval_events BEGIN SELECT RAISE(ABORT, 'approval_events is append-only'); END;
            """),
        (5, "phase5 assignments, events, inbox and outward actions", """
            ALTER TABLE turn_context ADD COLUMN assignment TEXT;
            ALTER TABLE usage ADD COLUMN assignment TEXT;
            CREATE INDEX usage_assignment ON usage(assignment);
            CREATE TABLE assignments (
              id TEXT PRIMARY KEY,
              root_id TEXT NOT NULL,
              parent_id TEXT,
              depth INTEGER NOT NULL,
              origin TEXT NOT NULL,
              origin_ref TEXT,
              requester_kind TEXT NOT NULL,
              requester_bot TEXT,
              requester_session TEXT,
              requester_turn TEXT,
              assignee_bot TEXT NOT NULL,
              assignee_session TEXT NOT NULL,
              session_id TEXT,
              mode TEXT NOT NULL,
              reply TEXT NOT NULL,
              brief TEXT NOT NULL,
              done_when TEXT,
              input TEXT,
              dedupe_key TEXT NOT NULL,
              rework INTEGER NOT NULL DEFAULT 0,
              joined_to TEXT,
              status TEXT NOT NULL,
              outcome TEXT,
              outcome_detail TEXT,
              level TEXT NOT NULL,
              budget_tokens INTEGER NOT NULL,
              budget_turns INTEGER NOT NULL,
              deadline TEXT,
              executions INTEGER NOT NULL DEFAULT 0,
              restarts INTEGER NOT NULL DEFAULT 0,
              continuations INTEGER NOT NULL DEFAULT 0,
              guard TEXT,
              tokens_used INTEGER NOT NULL DEFAULT 0,
              result TEXT,
              evidence TEXT,
              priority TEXT NOT NULL DEFAULT 'assignment',
              routine_run_id INTEGER,
              lease_owner TEXT,
              note TEXT,
              reported INTEGER NOT NULL DEFAULT 0,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              started_at TEXT,
              finished_at TEXT
            );
            CREATE INDEX assignments_root ON assignments(root_id);
            CREATE INDEX assignments_parent ON assignments(parent_id);
            CREATE INDEX assignments_status ON assignments(status);
            CREATE INDEX assignments_dedupe ON assignments(dedupe_key, created_at);
            CREATE TABLE assignment_events (
              id INTEGER PRIMARY KEY,
              assignment_id TEXT NOT NULL,
              at TEXT NOT NULL,
              from_status TEXT,
              to_status TEXT NOT NULL,
              actor TEXT NOT NULL,
              reason TEXT NOT NULL,
              data TEXT NOT NULL
            );
            CREATE INDEX assignment_events_a ON assignment_events(assignment_id, id);
            CREATE TRIGGER assignment_events_no_update BEFORE UPDATE ON assignment_events BEGIN SELECT RAISE(ABORT, 'assignment_events is append-only'); END;
            CREATE TRIGGER assignment_events_no_delete BEFORE DELETE ON assignment_events BEGIN SELECT RAISE(ABORT, 'assignment_events is append-only'); END;
            CREATE TABLE inbox (
              id INTEGER PRIMARY KEY,
              bot TEXT NOT NULL,
              assignment_id TEXT NOT NULL,
              kind TEXT NOT NULL,
              created_at TEXT NOT NULL,
              delivered_at TEXT,
              payload TEXT NOT NULL
            );
            CREATE INDEX inbox_bot ON inbox(bot, delivered_at);
            CREATE UNIQUE INDEX inbox_one_report ON inbox(assignment_id, kind) WHERE kind IN ('new_work', 'child_done');
            CREATE TABLE outward_actions (
              id INTEGER PRIMARY KEY,
              bot TEXT NOT NULL,
              assignment_id TEXT,
              session_id TEXT,
              lease_owner TEXT,
              tool TEXT NOT NULL,
              label TEXT NOT NULL,
              state TEXT NOT NULL,
              started_at TEXT NOT NULL,
              done_at TEXT
            );
            CREATE INDEX outward_assignment ON outward_actions(assignment_id);
            """),
        (6, "phase6 approval binding and bot versions", """
            ALTER TABLE approvals ADD COLUMN bound_to TEXT;
            ALTER TABLE approvals ADD COLUMN person_only INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE approvals ADD COLUMN action TEXT;
            CREATE TABLE bot_versions (
              id INTEGER PRIMARY KEY,
              bot TEXT NOT NULL,
              spec_json TEXT NOT NULL,
              spec_hash TEXT NOT NULL,
              changed_by TEXT NOT NULL,
              reason TEXT NOT NULL,
              at TEXT NOT NULL
            );
            CREATE INDEX bot_versions_bot ON bot_versions(bot, id);
            CREATE TRIGGER bot_versions_no_update BEFORE UPDATE ON bot_versions BEGIN SELECT RAISE(ABORT, 'bot_versions is append-only'); END;
            CREATE TRIGGER bot_versions_no_delete BEFORE DELETE ON bot_versions BEGIN SELECT RAISE(ABORT, 'bot_versions is append-only'); END;
            """),
        (7, "phase7 desktops and leases", """
            CREATE TABLE desktops (
              name TEXT PRIMARY KEY,
              mode TEXT NOT NULL,
              owner TEXT,
              port INTEGER NOT NULL UNIQUE,
              state TEXT NOT NULL,
              last_used_at TEXT,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );
            CREATE TABLE desktop_leases (
              id INTEGER PRIMARY KEY,
              desktop TEXT NOT NULL,
              bot TEXT NOT NULL,
              assignment_id TEXT,
              since TEXT NOT NULL,
              expires_at TEXT NOT NULL,
              released_at TEXT,
              reason TEXT
            );
            CREATE UNIQUE INDEX desktop_leases_active ON desktop_leases(desktop) WHERE released_at IS NULL;
            CREATE INDEX desktop_leases_bot ON desktop_leases(bot, released_at);
            """),
        (8, "phase9 rooms", """
            CREATE TABLE room_messages (
              id INTEGER PRIMARY KEY,
              room TEXT NOT NULL COLLATE NOCASE,
              author_kind TEXT NOT NULL,
              author TEXT NOT NULL,
              text TEXT NOT NULL,
              at TEXT NOT NULL,
              thread INTEGER,
              hop INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX room_messages_room ON room_messages(room, id);
            CREATE TRIGGER room_messages_no_update BEFORE UPDATE ON room_messages BEGIN SELECT RAISE(ABORT, 'room_messages is append-only'); END;
            CREATE TRIGGER room_messages_no_delete BEFORE DELETE ON room_messages BEGIN SELECT RAISE(ABORT, 'room_messages is append-only'); END;
            CREATE TABLE room_reads (
              room TEXT NOT NULL COLLATE NOCASE,
              bot TEXT NOT NULL,
              last_id INTEGER NOT NULL,
              PRIMARY KEY(room, bot)
            );
            CREATE TABLE room_turns (
              id INTEGER PRIMARY KEY,
              room TEXT NOT NULL COLLATE NOCASE,
              bot TEXT NOT NULL,
              thread INTEGER NOT NULL,
              trigger_id INTEGER NOT NULL,
              hop INTEGER NOT NULL,
              status TEXT NOT NULL,
              assignment_id TEXT,
              reason TEXT,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );
            CREATE INDEX room_turns_room ON room_turns(room, status, id);
            """),
        (9, "phase12 fleet", """
            CREATE TABLE fleet_nodes (
              name TEXT PRIMARY KEY,
              slot INTEGER NOT NULL UNIQUE,
              labels_json TEXT NOT NULL,
              never_touch INTEGER NOT NULL DEFAULT 0,
              state TEXT NOT NULL,
              fingerprint TEXT,
              token_hash TEXT,
              version TEXT,
              capacity INTEGER NOT NULL DEFAULT 1,
              last_heartbeat TEXT,
              created_by TEXT NOT NULL,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );
            CREATE TABLE fleet_invites (
              code_hash TEXT PRIMARY KEY,
              node TEXT NOT NULL,
              expires_at TEXT NOT NULL,
              used_at TEXT,
              created_by TEXT NOT NULL
            );
            CREATE TABLE fleet_jobs (
              id TEXT PRIMARY KEY,
              idempotency_key TEXT NOT NULL UNIQUE,
              kind TEXT NOT NULL,
              spec_json TEXT NOT NULL,
              spec_hash TEXT NOT NULL,
              state TEXT NOT NULL,
              approval_id TEXT,
              approved_by TEXT,
              requested_by TEXT NOT NULL,
              node TEXT,
              lease_id TEXT,
              lease_expires_at TEXT,
              fencing INTEGER NOT NULL DEFAULT 0,
              attempts INTEGER NOT NULL DEFAULT 0,
              result_json TEXT,
              reason TEXT,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL
            );
            CREATE INDEX fleet_jobs_state ON fleet_jobs(state, created_at);
            CREATE TABLE fleet_ledger (
              id INTEGER PRIMARY KEY,
              job_id TEXT NOT NULL,
              step_id TEXT NOT NULL,
              phase TEXT NOT NULL,
              outward INTEGER NOT NULL,
              node TEXT NOT NULL,
              fencing INTEGER NOT NULL,
              external_id TEXT,
              at TEXT NOT NULL,
              UNIQUE(job_id, step_id, phase)
            );
            CREATE TRIGGER fleet_ledger_no_update BEFORE UPDATE ON fleet_ledger BEGIN SELECT RAISE(ABORT, 'fleet_ledger is append-only'); END;
            CREATE TRIGGER fleet_ledger_no_delete BEFORE DELETE ON fleet_ledger BEGIN SELECT RAISE(ABORT, 'fleet_ledger is append-only'); END;
            CREATE TABLE fleet_artifacts (
              job_id TEXT NOT NULL,
              name TEXT NOT NULL,
              sha256 TEXT NOT NULL,
              size INTEGER NOT NULL,
              node TEXT NOT NULL,
              at TEXT NOT NULL,
              PRIMARY KEY(job_id, name)
            );
            CREATE TRIGGER fleet_artifacts_no_update BEFORE UPDATE ON fleet_artifacts BEGIN SELECT RAISE(ABORT, 'fleet_artifacts is append-only'); END;
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
