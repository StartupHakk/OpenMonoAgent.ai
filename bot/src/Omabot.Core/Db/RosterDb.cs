// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Data.Sqlite;
using Omabot.Common;

namespace Omabot.Db;

/// <summary>
/// roster.db access. One connection per call keeps it simple and safe across
/// threads. WAL mode, foreign keys on, busy timeout set. Schema changes are
/// ordered migrations (see Migrations.cs).
/// </summary>
public sealed class RosterDb
{
    private readonly string _connectionString;

    public RosterDb(string path)
    {
        Path_ = path;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (dir is not null && !Directory.Exists(dir)) SecureFile.EnsureDir(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();
        var isNew = !File.Exists(path);
        using (var c = Open())
        {
            Exec(c, "PRAGMA journal_mode=WAL;");
        }
        if (isNew && SecureFile.IsUnix) File.SetUnixFileMode(path, SecureFile.PrivateFile);
        Migrations.Apply(this);
    }

    public string Path_ { get; }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return c;
    }

    public static SqliteCommand Cmd(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public static int Exec(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Cmd(c, sql, args);
        return cmd.ExecuteNonQuery();
    }

    public int Exec(string sql, params (string Name, object? Value)[] args)
    {
        using var c = Open();
        return Exec(c, sql, args);
    }

    public static object? Scalar(SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Cmd(c, sql, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        using var c = Open();
        return Scalar(c, sql, args);
    }

    public static List<T> Query<T>(SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        using var cmd = Cmd(c, sql, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        using var c = Open();
        return Query(c, sql, map, args);
    }

    /// <summary>Run work in one transaction: all rows or none (UNIT-HO-08).</summary>
    public T InTransaction<T>(Func<SqliteConnection, SqliteTransaction, T> work)
    {
        using var c = Open();
        using var tx = c.BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            var result = work(c, tx);
            tx.Commit();
            return result;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public void InTransaction(Action<SqliteConnection, SqliteTransaction> work)
        => InTransaction<int>((c, tx) => { work(c, tx); return 0; });

    public static string? Str(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    public static long Long(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? 0 : r.GetInt64(i);
    }

    public static long? LongN(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }

    public static string Now(IClock clock) => clock.UtcNow.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
}
