// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Data.Sqlite;
using Omabot.Common;

namespace Omabot.Db;

/// <summary>
/// Append-only audit with a hash chain (plan 7.11, SEC-24). Every row hashes
/// the previous row, so editing or removing a row breaks the chain, which
/// doctor reports. Lines are redacted before they are stored or mirrored.
/// </summary>
public sealed class AuditLog(RosterDb db, IClock clock, Redactor redactor, string? mirrorDir = null)
{
    public const string Genesis = "genesis";

    public long Write(string actor, string action, string subject, string detail)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var id = Write(c, tx, actor, action, subject, detail);
        tx.Commit();
        return id;
    }

    public long Write(SqliteConnection c, SqliteTransaction tx, string actor, string action, string subject, string detail)
    {
        var at = RosterDb.Now(clock);
        actor = redactor.Redact(actor);
        subject = redactor.Redact(subject);
        detail = redactor.Redact(detail);
        using var prevCmd = RosterDb.Cmd(c, "SELECT hash FROM audit ORDER BY id DESC LIMIT 1;");
        prevCmd.Transaction = tx;
        var prev = prevCmd.ExecuteScalar() as string ?? Genesis;
        var hash = ComputeHash(prev, at, actor, action, subject, detail);
        using var cmd = RosterDb.Cmd(c,
            "INSERT INTO audit(at, actor, action, subject, detail, prev_hash, hash) VALUES ($at,$ac,$an,$s,$d,$p,$h); SELECT last_insert_rowid();",
            ("$at", at), ("$ac", actor), ("$an", action), ("$s", subject), ("$d", detail), ("$p", prev), ("$h", hash));
        cmd.Transaction = tx;
        var id = (long)(cmd.ExecuteScalar() ?? 0L);
        Mirror(at, actor, action, subject, detail);
        return id;
    }

    public static string ComputeHash(string prev, string at, string actor, string action, string subject, string detail)
        => SecureFile.Sha256Hex(string.Join("\u001f", prev, at, actor, action, subject, detail));

    private void Mirror(string at, string actor, string action, string subject, string detail)
    {
        if (mirrorDir is null) return;
        try
        {
            if (!Directory.Exists(mirrorDir)) SecureFile.EnsureDir(mirrorDir);
            var file = Path.Combine(mirrorDir, $"audit-{at[..10]}.log");
            var line = $"{at} {actor} {action} {subject} {detail.Replace('\n', ' ')}\n";
            var existed = File.Exists(file);
            File.AppendAllText(file, line);
            if (!existed && SecureFile.IsUnix) File.SetUnixFileMode(file, SecureFile.PrivateFile);
        }
        catch (IOException)
        {
            // The table is the record; the mirror is best effort.
        }
    }

    /// <summary>Check the whole chain. Returns null when intact, or the first broken row id with a reason.</summary>
    public (long Id, string Reason)? Verify()
    {
        var prev = Genesis;
        foreach (var row in db.Query("SELECT id, at, actor, action, subject, detail, prev_hash, hash FROM audit ORDER BY id;",
                     r => (Id: r.GetInt64(0), At: r.GetString(1), Actor: r.GetString(2), Action: r.GetString(3),
                         Subject: r.GetString(4), Detail: r.GetString(5), Prev: r.GetString(6), Hash: r.GetString(7))))
        {
            if (row.Prev != prev) return (row.Id, "previous hash does not match");
            var expect = ComputeHash(row.Prev, row.At, row.Actor, row.Action, row.Subject, row.Detail);
            if (expect != row.Hash) return (row.Id, "row hash does not match its content");
            prev = row.Hash;
        }
        return null;
    }

    public List<(long Id, string At, string Actor, string Action, string Subject, string Detail)> Tail(int count)
        => db.Query("SELECT id, at, actor, action, subject, detail FROM (SELECT * FROM audit ORDER BY id DESC LIMIT $n) ORDER BY id;",
            r => (r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)), ("$n", count));
}
