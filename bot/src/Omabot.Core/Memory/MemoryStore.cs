// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Omabot.Common;
using Omabot.Db;

namespace Omabot.Memory;

public static class MemoryStatus
{
    public const string Ok = "ok";
    public const string Conflict = "conflict";        // 409: the file changed since the revision named
    public const string MissingRev = "missing_rev";   // 428: no revision named
    public const string Invalid = "invalid";
    public const string TooLong = "too_long";
    public const string Full = "full";                // cannot fit even after moving every movable entry
    public const string Forgotten = "forgotten";      // capture tried to add back a forgotten fact
    public const string Unsafe = "unsafe";            // symlink or path outside the workspace
    public const string NotFound = "not_found";
}

public sealed record MemoryWriteResult(string Status, string Message, string? Rev = null, IReadOnlyList<string>? Moved = null, long? JournalId = null)
{
    public bool Ok => Status == MemoryStatus.Ok;
    public int HttpStatus => Status switch
    {
        MemoryStatus.Ok => 200,
        MemoryStatus.Conflict => 409,
        MemoryStatus.MissingRev => 428,
        MemoryStatus.NotFound => 404,
        MemoryStatus.Unsafe => 403,
        _ => 400,
    };
}

public sealed record JournalRow(long Id, string At, string Bot, string Actor, string File, string BeforeHash, string AfterHash, string Diff, string Reason, bool Undoable);

/// <summary>
/// One bot's memory files (plan 7.3). Every write is redacted before disk,
/// checked against the revision the writer read, kept inside the load budget,
/// written atomically with mode 0600 (archive before MEMORY.md) and journaled
/// outside the workspace so it can be undone.
/// </summary>
public sealed partial class MemoryStore
{
    public const string MemoryFile = "MEMORY.md";
    public const string ArchiveFile = "memory/archive.md";

    private readonly RosterDb _db;
    private readonly IClock _clock;
    private readonly Redactor _redactor;
    private readonly string _workspace;
    private readonly string _journalDir;
    private static readonly Lock WriteLock = new();

    public MemoryStore(RosterDb db, IClock clock, Redactor redactor, string slug, string workspace, string journalDir, MemoryLimits limits, string timezone)
    {
        _db = db;
        _clock = clock;
        _redactor = redactor;
        Slug = slug;
        _workspace = Path.GetFullPath(workspace);
        _journalDir = journalDir;
        Limits = limits;
        Zone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
    }

    public string Slug { get; }
    public MemoryLimits Limits { get; }
    public TimeZoneInfo Zone { get; }

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.UtcNow, Zone).DateTime);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,47}$")]
    private static partial Regex TopicName();

    public static string? TopicFile(string topic)
    {
        var t = topic.Trim().ToLowerInvariant();
        if (t.EndsWith(".md", StringComparison.Ordinal)) t = t[..^3];
        return TopicName().IsMatch(t) && t != "archive" && t != "log" ? "memory/" + t + ".md" : null;
    }

    /// <summary>Revision of a file: a short content hash, or "absent".</summary>
    public static string RevOf(string? text) => text is null ? "absent" : SecureFile.Sha256Hex(text)[..16];

    /// <summary>Full path for a memory file, or null if it is not a memory path or a link is in the way.</summary>
    public string? PathOf(string rel)
    {
        if (rel != MemoryFile && rel != ArchiveFile && (rel.StartsWith("memory/", StringComparison.Ordinal) ? TopicFile(rel[7..]) != rel : true)) return null;
        var full = Path.GetFullPath(Path.Combine(_workspace, rel));
        if (!full.StartsWith(_workspace + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
        // A bot could plant a link (MEMORY.md -> a roster secret). The roster never follows one.
        if (SecureFile.HasSymlinkSegment(full)) return null;
        return full;
    }

    /// <summary>Reads a memory file. Null text means the file does not exist; unsafe paths read as null with Unsafe set.</summary>
    public (string? Text, string Rev, bool Unsafe) Read(string rel)
    {
        var path = PathOf(rel);
        if (path is null) return (null, "absent", true);
        if (!File.Exists(path)) return (null, "absent", false);
        var text = SecureFile.NormalizeLf(SecureFile.ReadText(path));
        return (text, RevOf(text), false);
    }

    public LoadedMemory Load()
    {
        var (text, _, _) = Read(MemoryFile);
        return MemoryBudget.Load(text ?? "", Today, Limits);
    }

    /// <summary>Topic files with their titles and descriptions, for the prompt's topic index.</summary>
    public IReadOnlyList<(string Topic, string Title, string Description)> Topics()
    {
        var dir = PathOf("memory/x.md") is { } p ? Path.GetDirectoryName(p)! : null;
        if (dir is null || !Directory.Exists(dir)) return [];
        var list = new List<(string, string, string)>();
        foreach (var f in Directory.GetFiles(dir, "*.md").OrderBy(f => f, StringComparer.Ordinal))
        {
            var topic = Path.GetFileNameWithoutExtension(f);
            if (TopicFile(topic) is null || new FileInfo(f).LinkTarget is not null) continue;
            var (title, desc) = FrontMatter(SecureFile.ReadText(f));
            list.Add((topic, title ?? topic, desc ?? ""));
        }
        return list;
    }

    public string TopicIndex()
    {
        var topics = Topics();
        if (topics.Count == 0) return "(no topics yet)";
        return string.Join('\n', topics.Select(t => $"- {t.Topic}: {t.Title}" + (t.Description.Length > 0 ? " (" + t.Description + ")" : "")));
    }

    private static (string? Title, string? Description) FrontMatter(string text)
    {
        var lines = MemoryEntries.SplitLines(text);
        if (lines.Count == 0 || lines[0].Trim() != "---") return (null, null);
        string? title = null, desc = null;
        for (var i = 1; i < lines.Count && lines[i].Trim() != "---"; i++)
        {
            var l = lines[i];
            if (l.StartsWith("title:", StringComparison.Ordinal)) title = l[6..].Trim().Trim('"');
            else if (l.StartsWith("description:", StringComparison.Ordinal)) desc = l[12..].Trim().Trim('"');
        }
        return (title, desc);
    }

    // ---------- writes ----------

    /// <summary>
    /// Adds an entry to MEMORY.md (or a topic file). Redacts first, checks
    /// limits and the revision, then keeps MEMORY.md inside the budget by
    /// moving entries to the archive in the same step.
    /// </summary>
    public MemoryWriteResult AddEntry(string fact, string source, DateOnly? until, string? topic, string? expectedRev, string actor, string reason)
    {
        var rel = MemoryFile;
        if (topic is not null)
        {
            rel = TopicFile(topic) ?? "";
            if (rel.Length == 0) return new(MemoryStatus.Invalid, $"\"{topic}\" is not a topic name (lowercase letters, digits and hyphens).");
        }
        var clean = _redactor.Redact(SecureFile.NormalizeLf(fact)).Trim();
        if (MemoryEntries.CheckSize(clean) is { } bad) return new(MemoryStatus.TooLong, bad);
        if (clean.Contains('\u00b7')) clean = clean.Replace('\u00b7', '.');
        if (until is { } u && u < Today) return new(MemoryStatus.Invalid, "The until date is already past.");
        var src = Regex.Replace(source.Trim(), @"[\u00b7\n]", " ");
        if (!src.StartsWith("from ", StringComparison.Ordinal)) src = "from " + src;
        var line = MemoryEntries.NewEntry(Today, src, clean, until);
        return Mutate(rel, expectedRev, actor, reason, current =>
        {
            var body = current ?? (rel == MemoryFile ? "" : $"---\ntitle: {topic}\n---\n");
            return (body.Length == 0 || body.EndsWith('\n') ? body : body + "\n") + line + "\n";
        });
    }

    /// <summary>
    /// Removes entries whose fact matches (same fact rule) and records the
    /// fact so capture never adds it back.
    /// </summary>
    public MemoryWriteResult Forget(string fact, string? topic, string? expectedRev, string actor)
    {
        var rel = topic is null ? MemoryFile : TopicFile(topic) ?? "";
        if (rel.Length == 0) return new(MemoryStatus.Invalid, $"\"{topic}\" is not a topic name.");
        var removed = 0;
        var result = Mutate(rel, expectedRev, actor, "forget", current =>
        {
            if (current is null) return null;
            var blocks = MemoryEntries.Parse(current);
            var keep = blocks.Where(b => !(b.Entry is { } e ? MemoryEntries.SameFact(e.Fact, fact) : MemoryEntries.SameFact(b.Text, fact))).ToList();
            removed = blocks.Count - keep.Count;
            return removed == 0 ? null : MemoryEntries.Join(keep);
        });
        if (result.Status == MemoryStatus.Ok || result.Status == MemoryStatus.NotFound)
        {
            _db.Exec("INSERT INTO memory_forgotten(bot, fact_norm, at) VALUES($b,$f,$a);",
                ("$b", Slug), ("$f", MemoryEntries.Normalize(_redactor.Redact(fact))), ("$a", RosterDb.Now(_clock)));
        }
        if (result.Status == MemoryStatus.NotFound) return result with { Message = "No entry matched; the fact is recorded as forgotten so capture will not add it." };
        return result with { Message = $"Removed {removed} entr{(removed == 1 ? "y" : "ies")}; capture will not add it back." };
    }

    public bool IsForgotten(string fact)
        => Convert.ToInt64(_db.Scalar("SELECT COUNT(*) FROM memory_forgotten WHERE bot=$b AND fact_norm=$f;", ("$b", Slug), ("$f", MemoryEntries.Normalize(fact))) ?? 0L) > 0;

    /// <summary>Replaces a whole memory file (web editor, upkeep). Same revision, redaction and budget rules.</summary>
    public MemoryWriteResult WriteFile(string rel, string text, string? expectedRev, string actor, string reason)
        => Mutate(rel, expectedRev, actor, reason, _ => text);

    /// <summary>
    /// The one write path. <paramref name="change"/> returns the new text or
    /// null for "nothing to do". The revision check, redaction, budget fit,
    /// archive-first write order and journal rows all happen here.
    /// </summary>
    internal MemoryWriteResult Mutate(string rel, string? expectedRev, string actor, string reason, Func<string?, string?> change)
    {
        if (expectedRev is null) return new(MemoryStatus.MissingRev, "Name the revision you read (rev). Read the file first.");
        lock (WriteLock)
        {
            var path = PathOf(rel);
            if (path is null) return new(MemoryStatus.Unsafe, $"{rel} is not a memory file this tool can write, or a link is in the way.");
            var (current, rev, _) = Read(rel);
            if (!string.Equals(rev, expectedRev, StringComparison.Ordinal))
                return new(MemoryStatus.Conflict, $"{rel} changed since you read it (now rev {rev}). Read it again and retry.", rev);
            var next = change(current);
            if (next is null) return new(MemoryStatus.NotFound, "Nothing to change.", rev);
            next = _redactor.Redact(SecureFile.NormalizeLf(next));
            List<string> moved = [];
            if (rel == MemoryFile)
            {
                var fit = MemoryBudget.Fit(next, Today, Limits);
                if (!fit.Fits) return new(MemoryStatus.Full, $"MEMORY.md would pass {Limits.MaxLines} lines or {Limits.MaxBytes} bytes and nothing else can move. Forget or shorten an entry first.", rev);
                next = fit.Text;
                moved = [.. fit.Moved];
            }
            else if (rel != ArchiveFile && !Limits.Fits(next) && MemoryEntries.ByteCount(next) > 256 * 1024)
            {
                return new(MemoryStatus.TooLong, "Topic files are limited to 256 KB.", rev);
            }
            if (next == current) return new(MemoryStatus.Ok, "No change.", rev, []);
            if (moved.Count > 0)
            {
                // Archive first, so a line is never missing from both files.
                var (arch, _, archUnsafe) = Read(ArchiveFile);
                if (archUnsafe) return new(MemoryStatus.Unsafe, "memory/archive.md is a link; the roster will not follow it.", rev);
                var archText = (arch ?? "# Archive\n") + string.Join('\n', moved) + "\n";
                WriteJournaled(ArchiveFile, arch, archText, actor, $"moved {moved.Count} from MEMORY.md ({reason})", true);
            }
            var id = WriteJournaled(rel, current, next, actor, reason, true);
            var msg = moved.Count == 0 ? "Saved." : $"Saved. Moved {moved.Count} older entr{(moved.Count == 1 ? "y" : "ies")} to memory/archive.md to stay inside the budget.";
            return new(MemoryStatus.Ok, msg, RevOf(next), moved, id);
        }
    }

    private long WriteJournaled(string rel, string? before, string after, string actor, string reason, bool undoable)
    {
        var path = PathOf(rel) ?? throw new IOException("unsafe memory path " + rel);
        SecureFile.EnsureDirNoClobber(Path.GetDirectoryName(path)!);
        SecureFile.WriteAllTextAtomic(path, after);
        return Journal(rel, before, after, actor, reason, undoable);
    }

    internal long Journal(string rel, string? before, string? after, string actor, string reason, bool undoable)
    {
        var diff = Diff(before, after);
        var at = RosterDb.Now(_clock);
        var id = _db.InTransaction((c, tx) =>
        {
            RosterDb.Exec(c, """
                INSERT INTO memory_journal(at, bot, actor, file, before_hash, after_hash, diff, reason, before_text, after_text, undoable)
                VALUES($at,$b,$ac,$f,$bh,$ah,$d,$r,$bt,$at2,$u);
                """, ("$at", at), ("$b", Slug), ("$ac", actor), ("$f", rel), ("$bh", RevOf(before)), ("$ah", RevOf(after)),
                ("$d", diff), ("$r", _redactor.Redact(reason)), ("$bt", undoable ? before : null), ("$at2", after), ("$u", undoable ? 1 : 0));
            return (long)RosterDb.Scalar(c, "SELECT last_insert_rowid();")!;
        });
        SecureFile.EnsureDirNoClobber(_journalDir);
        var row = JsonSerializer.Serialize(new { id, at, bot = Slug, actor, file = rel, before_hash = RevOf(before), after_hash = RevOf(after), diff, reason = _redactor.Redact(reason), undoable });
        SecureFile.WriteAllTextAtomic(Path.Combine(_journalDir, $"memory-{id:D8}.json"), row + "\n");
        return id;
    }

    /// <summary>A small line diff: removed lines with "-", added lines with "+".</summary>
    public static string Diff(string? before, string? after)
    {
        var a = MemoryEntries.SplitLines(before ?? "");
        var b = MemoryEntries.SplitLines(after ?? "");
        var setA = a.ToHashSet(StringComparer.Ordinal);
        var setB = b.ToHashSet(StringComparer.Ordinal);
        var lines = a.Where(l => !setB.Contains(l)).Select(l => "-" + l).Concat(b.Where(l => !setA.Contains(l)).Select(l => "+" + l)).Take(400);
        return string.Join('\n', lines);
    }

    public IReadOnlyList<JournalRow> JournalRows(int limit = 50)
        => _db.Query("SELECT id, at, bot, actor, file, before_hash, after_hash, diff, reason, undoable FROM memory_journal WHERE bot=$b ORDER BY id DESC LIMIT $n;",
            r => new JournalRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetInt64(9) == 1),
            ("$b", Slug), ("$n", limit));

    /// <summary>
    /// Restores the before state of one journal row, when the file still has
    /// that row's after state. Otherwise refuses and says why (UNIT-MEM-07).
    /// </summary>
    public MemoryWriteResult Undo(long journalId, string actor)
    {
        var row = _db.Query("SELECT file, after_hash, before_text, before_hash, undoable FROM memory_journal WHERE id=$i AND bot=$b;",
            r => (File: r.GetString(0), After: r.GetString(1), Before: RosterDb.Str(r, "before_text"), BeforeHash: r.GetString(3), Undoable: r.GetInt64(4) == 1),
            ("$i", journalId), ("$b", Slug)).FirstOrDefault();
        if (row.File is null) return new(MemoryStatus.NotFound, $"No journal row {journalId} for {Slug}.");
        if (!row.Undoable) return new(MemoryStatus.Invalid, $"Journal row {journalId} cannot be undone (its before state held a secret and was not kept).");
        lock (WriteLock)
        {
            var (current, rev, unsafePath) = Read(row.File);
            if (unsafePath) return new(MemoryStatus.Unsafe, $"{row.File} is a link; the roster will not follow it.");
            if (rev != row.After) return new(MemoryStatus.Conflict, $"{row.File} changed since journal row {journalId} (now rev {rev}, expected {row.After}). Undo the later rows first.", rev);
            var path = PathOf(row.File)!;
            if (row.Before is null)
            {
                if (row.BeforeHash != "absent") return new(MemoryStatus.Invalid, $"Journal row {journalId} has no before state.");
                File.Delete(path);
            }
            else
            {
                SecureFile.WriteAllTextAtomic(path, row.Before);
            }
            var id = Journal(row.File, current, row.Before, actor, $"undo {journalId}", true);
            return new(MemoryStatus.Ok, $"Restored {row.File} to its state before journal row {journalId}.", RevOf(row.Before), [], id);
        }
    }

    /// <summary>
    /// Backstop for direct file writes (post tool hook flag or the watcher):
    /// any memory file holding a secret is rewritten through the redactor and
    /// journaled as "redacted after direct write". The before state is not
    /// kept, because it holds the secret. Returns the files fixed.
    /// </summary>
    public IReadOnlyList<string> ScanDirectWrites()
    {
        var fixedFiles = new List<string>();
        var rels = new List<string> { MemoryFile, ArchiveFile };
        rels.AddRange(Topics().Select(t => "memory/" + t.Topic + ".md"));
        lock (WriteLock)
        {
            foreach (var rel in rels.Distinct())
            {
                var (text, _, unsafePath) = Read(rel);
                if (unsafePath || text is null) continue;
                var clean = _redactor.Redact(text);
                if (clean == text) continue;
                SecureFile.WriteAllTextAtomic(PathOf(rel)!, clean);
                Journal(rel, text, clean, "direct write", "redacted after direct write", undoable: false);
                fixedFiles.Add(rel);
            }
        }
        return fixedFiles;
    }

    /// <summary>One line in today's daily log, memory/log/DATE.md (for Spencer; never loaded).</summary>
    public void AppendDailyLog(string line)
    {
        var dir = Path.Combine(_workspace, "memory", "log");
        if (SecureFile.HasSymlinkSegment(Path.Combine(dir, "x"))) return;
        SecureFile.EnsureDirNoClobber(dir);
        var path = Path.Combine(dir, MemoryEntries.Day(Today) + ".md");
        if (File.Exists(path) && new FileInfo(path).LinkTarget is not null) return;
        var local = TimeZoneInfo.ConvertTime(_clock.UtcNow, Zone);
        var text = (File.Exists(path) ? SecureFile.ReadText(path) : $"# {MemoryEntries.Day(Today)}\n") + local.ToString("HH:mm", CultureInfo.InvariantCulture) + " " + _redactor.Redact(line).Replace('\n', ' ') + "\n";
        SecureFile.WriteAllTextAtomic(path, text);
    }
}
