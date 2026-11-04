// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Microsoft.Data.Sqlite;
using Omabot.Common;
using Omabot.Db;

namespace Omabot.Web;

/// <summary>
/// users.db: web accounts, sessions and one-time login links (plan 7.19),
/// kept apart from roster.db. 0600, WAL, ordered migrations. Only hashes of
/// session ids, login codes and passwords are stored.
/// </summary>
public sealed class UsersDb
{
    private readonly string _cs;

    private static readonly (int Version, string Sql)[] Steps =
    [
        (1, """
            CREATE TABLE sessions (
              id_hash TEXT PRIMARY KEY,
              user_id TEXT NOT NULL,
              name TEXT NOT NULL,
              role TEXT NOT NULL,
              csrf TEXT NOT NULL,
              created_at TEXT NOT NULL,
              expires_at TEXT NOT NULL,
              last_seen_at TEXT NOT NULL,
              ip TEXT
            );
            CREATE TABLE login_codes (
              code_hash TEXT PRIMARY KEY,
              user_id TEXT NOT NULL,
              name TEXT NOT NULL,
              role TEXT NOT NULL,
              expires_at TEXT NOT NULL,
              used_at TEXT
            );
            """),
        (2, """
            CREATE TABLE accounts (
              name TEXT PRIMARY KEY,
              role TEXT NOT NULL,
              state TEXT NOT NULL,
              password_hash TEXT NOT NULL,
              totp_seed TEXT NOT NULL,
              totp_last_step INTEGER NOT NULL DEFAULT 0,
              failed INTEGER NOT NULL DEFAULT 0,
              locked_until TEXT,
              lockouts INTEGER NOT NULL DEFAULT 0,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              last_login_at TEXT
            );
            CREATE TABLE login_failures (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              name TEXT NOT NULL,
              ip TEXT NOT NULL,
              at TEXT NOT NULL
            );
            CREATE INDEX login_failures_ip ON login_failures(ip, at);
            CREATE TABLE address_locks (
              ip TEXT PRIMARY KEY,
              locked_until TEXT NOT NULL,
              lockouts INTEGER NOT NULL
            );
            """),
        (3, """
            ALTER TABLE accounts ADD COLUMN home TEXT;
            CREATE TABLE homes (
              name TEXT PRIMARY KEY,
              idx INTEGER NOT NULL UNIQUE,
              created_at TEXT NOT NULL
            );
            """),
    ];

    public UsersDb(string path)
    {
        Path = path;
        SecureFile.EnsureDirNoClobber(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var isNew = !File.Exists(path);
        _cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, Pooling = false }.ToString();
        using (var c = Open())
        {
            RosterDb.Exec(c, "PRAGMA journal_mode=WAL;");
            RosterDb.Exec(c, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);");
            var have = Convert.ToInt32(RosterDb.Scalar(c, "SELECT COALESCE(MAX(version), 0) FROM schema_version;") ?? 0, CultureInfo.InvariantCulture);
            foreach (var (v, sql) in Steps.Where(s => s.Version > have).OrderBy(s => s.Version))
            {
                using var tx = c.BeginTransaction();
                using (var cmd = RosterDb.Cmd(c, sql)) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                using (var cmd = RosterDb.Cmd(c, "INSERT INTO schema_version(version, applied_at) VALUES ($v, $t);", ("$v", v), ("$t", DateTimeOffset.UtcNow.ToString("O")))) { cmd.Transaction = tx; cmd.ExecuteNonQuery(); }
                tx.Commit();
            }
        }
        if (isNew && SecureFile.IsUnix) File.SetUnixFileMode(path, SecureFile.PrivateFile);
    }

    public string Path { get; }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_cs);
        c.Open();
        RosterDb.Exec(c, "PRAGMA busy_timeout=5000;");
        return c;
    }

    public int Exec(string sql, params (string, object?)[] args)
    {
        using var c = Open();
        return RosterDb.Exec(c, sql, args);
    }

    public object? Scalar(string sql, params (string, object?)[] args)
    {
        using var c = Open();
        return RosterDb.Scalar(c, sql, args);
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        using var c = Open();
        using var cmd = RosterDb.Cmd(c, sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }
}
