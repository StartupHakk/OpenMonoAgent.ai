// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using Omabot.Common;
using Omabot.Db;

namespace Omabot.Memory;

/// <summary>A model call made at background priority on a bot's behalf. Null means dropped or failed.</summary>
public interface IUpkeepModel
{
    Task<string?> CompleteAsync(string bot, string system, string user, CancellationToken ct);
}

public sealed record TurnRecord(string At, string Person, string Bot, bool Ok);

public sealed record UpkeepReport(string Job, int Changed, string Note);

/// <summary>
/// Memory upkeep (plan 7.3): capture, organize and nightly tidy. Every
/// change goes through <see cref="MemoryStore"/>, so it is redacted,
/// revision checked, inside the budget and journaled with undo.
/// </summary>
public sealed class MemoryUpkeep(MemoryStore store, RosterDb db, IClock clock, IUpkeepModel? model, string turnsFile)
{
    public const int CaptureQuietMinutes = 2;
    public const int CaptureAfterTurns = 6;
    public const int OrganizeMaxLines = 20;
    public const int MaxCaptureFacts = 5;

    // ---------- turn log and capture triggers ----------

    /// <summary>Records one finished turn Spencer started (routine runs, bot-started and failed turns are not captured).</summary>
    public void RecordPersonTurn(string person, string bot, bool ok)
    {
        SecureFile.EnsureDirNoClobber(Path.GetDirectoryName(turnsFile)!);
        var line = JsonSerializer.Serialize(new TurnRecord(RosterDb.Now(clock), Redactor.Shared.Redact(person), Redactor.Shared.Redact(bot), ok));
        File.AppendAllText(turnsFile, line + "\n");
        SecureFile.TrySetMode(turnsFile);
        db.Exec("""
            INSERT INTO memory_state(bot, last_turn_at, turns_since) VALUES($b,$t,$n)
            ON CONFLICT(bot) DO UPDATE SET last_turn_at=$t, turns_since=turns_since+$n;
            """, ("$b", store.Slug), ("$t", RosterDb.Now(clock)), ("$n", ok ? 1 : 0));
    }

    public bool CaptureDue()
    {
        var row = db.Query("SELECT last_turn_at, turns_since FROM memory_state WHERE bot=$b;", r => (RosterDb.Str(r, "last_turn_at"), r.GetInt64(1)), ("$b", store.Slug)).FirstOrDefault();
        if (row.Item1 is null || row.Item2 == 0) return false;
        if (row.Item2 >= CaptureAfterTurns) return true;
        return clock.UtcNow - DateTimeOffset.Parse(row.Item1, System.Globalization.CultureInfo.InvariantCulture) >= TimeSpan.FromMinutes(CaptureQuietMinutes);
    }

    public bool TidyDue()
    {
        var local = TimeZoneInfo.ConvertTime(clock.UtcNow, store.Zone);
        if (local.Hour < 3) return false;
        var last = db.Scalar("SELECT last_tidy_day FROM memory_state WHERE bot=$b;", ("$b", store.Slug)) as string;
        return last != MemoryEntries.Day(store.Today);
    }

    private IReadOnlyList<TurnRecord> TurnsSince(string? since)
    {
        if (!File.Exists(turnsFile)) return [];
        return File.ReadLines(turnsFile)
            .Select(l => { try { return JsonSerializer.Deserialize<TurnRecord>(l); } catch (JsonException) { return null; } })
            .Where(t => t is not null && t.Ok && (since is null || string.CompareOrdinal(t.At, since) > 0))
            .TakeLast(12).ToList()!;
    }

    // ---------- capture ----------

    public const string CaptureSystem = """
        You file durable facts for an assistant's memory. Read the chat turns and list new facts worth remembering
        (preferences, decisions, paths, names). Each fact must rest on words Spencer actually wrote: give them as
        "quote", copied exactly. Never file secrets. Reply with JSON only:
        {"facts":[{"fact":"...","quote":"...","until":null}]}
        Reply {"facts":[]} when nothing is worth keeping.
        """;

    public async Task<UpkeepReport> CaptureAsync(CancellationToken ct)
    {
        var since = db.Scalar("SELECT last_capture_at FROM memory_state WHERE bot=$b;", ("$b", store.Slug)) as string;
        var turns = TurnsSince(since);
        void Done() => db.Exec("UPDATE memory_state SET turns_since=0, last_capture_at=$t WHERE bot=$b;", ("$b", store.Slug), ("$t", RosterDb.Now(clock)));
        if (turns.Count == 0 || model is null) { Done(); return new("capture", 0, "nothing to read"); }
        var user = string.Join("\n\n", turns.Select(t => $"Spencer: {t.Person}\nAssistant: {Trim(t.Bot, 1500)}"));
        var reply = await model.CompleteAsync(store.Slug, CaptureSystem, user, ct).ConfigureAwait(false);
        if (reply is null) return new("capture", 0, "model call dropped; will retry");
        Done();
        var said = string.Join("\n", turns.Select(t => t.Person));
        var added = 0;
        foreach (var (fact, quote, until) in ParseFacts(reply).Take(MaxCaptureFacts))
        {
            if (!QuoteBacked(quote, said) || store.IsForgotten(fact) || AlreadyKnown(fact)) continue;
            var (_, rev, _) = store.Read(MemoryStore.MemoryFile);
            var r = store.AddEntry($"{fact} (said: \"{quote}\")", "from chat", until, null, rev, "upkeep", "capture");
            if (r.Ok) added++;
        }
        return new("capture", added, $"{added} fact(s) filed");
    }

    public static bool QuoteBacked(string quote, string said)
    {
        static string N(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        return quote.Trim().Length >= 3 && N(said).Contains(N(quote), StringComparison.Ordinal);
    }

    private bool AlreadyKnown(string fact)
    {
        var (text, _, _) = store.Read(MemoryStore.MemoryFile);
        return MemoryEntries.Parse(text ?? "").Any(b => b.Entry is { } e && (MemoryEntries.SameFact(e.Fact, fact) || e.Fact.StartsWith(fact + " (said:", StringComparison.Ordinal)));
    }

    public static IEnumerable<(string Fact, string Quote, DateOnly? Until)> ParseFacts(string reply)
    {
        var json = ExtractJson(reply);
        if (json?["facts"] is not JsonArray arr) yield break;
        foreach (var n in arr)
        {
            var fact = n?["fact"]?.GetValue<string>()?.Trim();
            var quote = n?["quote"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrEmpty(fact) || string.IsNullOrEmpty(quote) || fact.Length > 300) continue;
            DateOnly? until = DateOnly.TryParse(n?["until"]?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var u) ? u : null;
            yield return (fact.Replace('\n', ' '), quote.Replace('\n', ' '), until);
        }
    }

    public static JsonNode? ExtractJson(string reply)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonNode.Parse(reply[start..(end + 1)]); }
        catch (JsonException) { return null; }
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "...";

    // ---------- nightly tidy ----------

    public const string ConflictSystem = """
        Each numbered line is a remembered fact. Find pairs that cannot both be true. Reply with JSON only:
        {"pairs":[[older_number, newer_number]]}. Reply {"pairs":[]} if none.
        """;

    /// <summary>
    /// Moves expired entries to the archive, removes exact duplicates (newer
    /// stays) and strikes the older of two facts that cannot both be true. At
    /// most a fifth of entries are struck per pass, none in a file with fewer
    /// than 5 entries.
    /// </summary>
    public async Task<UpkeepReport> TidyAsync(CancellationToken ct)
    {
        db.Exec("INSERT INTO memory_state(bot, last_tidy_day) VALUES($b,$d) ON CONFLICT(bot) DO UPDATE SET last_tidy_day=$d;", ("$b", store.Slug), ("$d", MemoryEntries.Day(store.Today)));
        var (text, rev, _) = store.Read(MemoryStore.MemoryFile);
        if (text is null) return new("tidy", 0, "no MEMORY.md");
        var (tidied, expired, dupes) = TidyText(text, store.Today);
        var changed = 0;
        if (tidied != text)
        {
            if (expired.Count > 0)
            {
                var (arch, arev, _) = store.Read(MemoryStore.ArchiveFile);
                store.WriteFile(MemoryStore.ArchiveFile, (arch ?? "# Archive\n") + string.Join('\n', expired) + "\n", arev, "upkeep", "tidy: expired entries");
            }
            var r = store.WriteFile(MemoryStore.MemoryFile, tidied, rev, "upkeep", $"tidy: {expired.Count} expired, {dupes} duplicate(s)");
            if (r.Ok) { changed += expired.Count + dupes; text = tidied; rev = r.Rev!; }
        }
        if (model is not null)
        {
            var live = MemoryEntries.Parse(text).Where(b => b.Entry is { Struck: false } e && !e.IsSafety).ToList();
            var cap = StrikeCap(MemoryEntries.Parse(text).Count(b => b.IsEntry));
            if (cap > 0 && live.Count >= 2)
            {
                var user = string.Join('\n', live.Select((b, i) => $"{i + 1}. ({MemoryEntries.Day(b.Entry!.Date)}) {b.Entry!.Fact}"));
                var reply = await model.CompleteAsync(store.Slug, ConflictSystem, user, ct).ConfigureAwait(false);
                var strike = new HashSet<int>();
                if (reply is not null && ExtractJson(reply)?["pairs"] is JsonArray pairs)
                {
                    foreach (var p in pairs)
                    {
                        if (p is not JsonArray pa || pa.Count != 2) continue;
                        if (!int.TryParse(pa[0]?.ToString(), out var a) || !int.TryParse(pa[1]?.ToString(), out var b)) continue;
                        if (a < 1 || b < 1 || a > live.Count || b > live.Count || a == b) continue;
                        var older = live[a - 1].Entry!.Date <= live[b - 1].Entry!.Date ? a : b;
                        if (strike.Count < cap) strike.Add(older - 1);
                    }
                }
                if (strike.Count > 0)
                {
                    var targets = strike.Select(i => live[i]).ToHashSet();
                    var blocks = MemoryEntries.Parse(text).Select(b => targets.Any(t => t.Order == b.Order)
                        ? new MemoryBlock([MemoryEntries.Format(b.Entry! with { Struck = true }), .. b.Lines.Skip(1)], b.Order) { Entry = b.Entry with { Struck = true } }
                        : b);
                    var r = store.WriteFile(MemoryStore.MemoryFile, MemoryEntries.Join(blocks), rev, "upkeep", $"tidy: struck {strike.Count} contradicted entr{(strike.Count == 1 ? "y" : "ies")}");
                    if (r.Ok) changed += strike.Count;
                }
            }
        }
        return new("tidy", changed, $"{changed} change(s)");
    }

    /// <summary>At most a fifth of entries per pass, none when the file has fewer than 5.</summary>
    public static int StrikeCap(int entries) => entries < 5 ? 0 : entries / 5;

    /// <summary>The model-free part of tidy: expired entries out, exact duplicates removed (newer stays).</summary>
    public static (string Text, List<string> Expired, int Duplicates) TidyText(string text, DateOnly today)
    {
        var blocks = MemoryEntries.Parse(text);
        var expired = new List<string>();
        var keep = new List<MemoryBlock>();
        foreach (var b in blocks)
        {
            if (b.Entry is { } e && e.ExpiredOn(today) && !e.IsSafety) expired.Add(MemoryBudget.MarkMoved(b, today));
            else keep.Add(b);
        }
        var dupes = 0;
        var newestByFact = new Dictionary<string, MemoryBlock>(StringComparer.Ordinal);
        foreach (var b in keep.Where(b => b.Entry is { Struck: false }))
        {
            var key = MemoryEntries.Normalize(b.Entry!.Fact);
            if (!newestByFact.TryGetValue(key, out var prev) || b.Entry.Date >= prev.Entry!.Date) newestByFact[key] = b;
        }
        var result = keep.Where(b =>
        {
            if (b.Entry is not { Struck: false } e) return true;
            var winner = newestByFact[MemoryEntries.Normalize(e.Fact)];
            if (ReferenceEquals(winner, b)) return true;
            dupes++;
            return false;
        }).ToList();
        return (MemoryEntries.Join(result), expired, dupes);
    }

    // ---------- organize ----------

    public const string OrganizeSystem = """
        Each numbered line is a detail from an assistant's main memory file. Pick details that belong in a topic file
        (a short lowercase topic name such as "projects" or "people"). Reply with JSON only:
        {"moves":[{"line":1,"topic":"projects"}]}. Reply {"moves":[]} to move nothing.
        """;

    /// <summary>
    /// Moves detail lines from MEMORY.md into topic files, at most 20 per pass.
    /// Core facts (undated, health and safety) never move. Runs only when
    /// MEMORY.md is past half its budget (decision B27).
    /// </summary>
    public async Task<UpkeepReport> OrganizeAsync(CancellationToken ct)
    {
        var (text, rev, _) = store.Read(MemoryStore.MemoryFile);
        if (text is null || model is null) return new("organize", 0, "nothing to do");
        if (MemoryEntries.LineCount(text) * 2 <= store.Limits.MaxLines && MemoryEntries.ByteCount(text) * 2 <= store.Limits.MaxBytes)
            return new("organize", 0, "under half the budget");
        var blocks = MemoryEntries.Parse(text);
        var movable = blocks.Where(b => b.Entry is { Struck: false } e && !e.IsSafety).ToList();
        var reply = await model.CompleteAsync(store.Slug, OrganizeSystem, string.Join('\n', movable.Select((b, i) => $"{i + 1}. {b.Entry!.Fact}")), ct).ConfigureAwait(false);
        if (reply is null || ExtractJson(reply)?["moves"] is not JsonArray moves) return new("organize", 0, "no plan");
        var byTopic = new Dictionary<string, List<MemoryBlock>>();
        var moved = new HashSet<int>();
        foreach (var m in moves)
        {
            if (moved.Count >= OrganizeMaxLines) break;
            if (!int.TryParse(m?["line"]?.ToString(), out var n) || n < 1 || n > movable.Count) continue;
            var topic = m?["topic"]?.GetValue<string>() ?? "";
            if (MemoryStore.TopicFile(topic) is null || !moved.Add(movable[n - 1].Order)) continue;
            byTopic.TryAdd(topic, []);
            byTopic[topic].Add(movable[n - 1]);
        }
        foreach (var (topic, list) in byTopic)
        {
            var rel = MemoryStore.TopicFile(topic)!;
            var (t, trev, _) = store.Read(rel);
            var body = t ?? $"---\ntitle: {topic}\n---\n";
            var r = store.WriteFile(rel, body + string.Join('\n', list.Select(b => b.Text)) + "\n", trev, "upkeep", "organize: from MEMORY.md");
            if (!r.Ok) list.ForEach(b => moved.Remove(b.Order));
        }
        if (moved.Count == 0) return new("organize", 0, "nothing moved");
        var w = store.WriteFile(MemoryStore.MemoryFile, MemoryEntries.Join(blocks.Where(b => !moved.Contains(b.Order))), rev, "upkeep", $"organize: {moved.Count} line(s) to topic files");
        return new("organize", w.Ok ? moved.Count : 0, w.Message);
    }
}
