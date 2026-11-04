// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Omabot.Memory;

/// <summary>
/// One block of a memory file. A dated entry is one line
/// "- 2026-11-02 · from chat · text [· until DATE] [· moved DATE]" plus up to
/// 19 continuation lines indented by two spaces. A struck entry wraps the
/// line body in ~~. Everything else (headings, blank lines, lines Spencer
/// wrote by hand without a date) is a fixed block that never moves.
/// </summary>
public sealed record MemoryBlock(IReadOnlyList<string> Lines, int Order)
{
    public string Text => string.Join('\n', Lines);
    public MemoryEntry? Entry { get; init; }
    public bool IsEntry => Entry is not null;
}

public sealed record MemoryEntry(DateOnly Date, string Source, string Fact, DateOnly? Until, DateOnly? Moved, bool Struck)
{
    /// <summary>Health and safety facts never move automatically (plan 7.3, decision B25).</summary>
    public bool IsSafety => MemoryEntries.IsSafetyText(Fact);

    public bool ExpiredOn(DateOnly today) => Until is { } u && u < today;
}

public static partial class MemoryEntries
{
    public const string Sep = " \u00b7 ";
    public const int MaxEntryChars = 1000;
    public const int MaxEntryLines = 20;

    [GeneratedRegex(@"^- (?<struck>~~)?(?<date>\d{4}-\d{2}-\d{2}) \u00b7 (?<src>from [^\u00b7\n]+?) \u00b7 (?<rest>.+?)(?:~~)?$")]
    private static partial Regex EntryLine();

    [GeneratedRegex(@" \u00b7 until (?<d>\d{4}-\d{2}-\d{2})")]
    private static partial Regex UntilPart();

    [GeneratedRegex(@" \u00b7 moved (?<d>\d{4}-\d{2}-\d{2})")]
    private static partial Regex MovedPart();

    [GeneratedRegex(@"(?i)(^|\W)(\[(safety|health)\]|safety:|health:)")]
    private static partial Regex SafetyMark();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public static bool IsSafetyText(string text) => SafetyMark().IsMatch(text);

    public static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static MemoryEntry? ParseLine(string line)
    {
        var m = EntryLine().Match(line.TrimEnd());
        if (!m.Success) return null;
        if (!DateOnly.TryParseExact(m.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return null;
        var rest = m.Groups["rest"].Value;
        DateOnly? until = null, moved = null;
        var mm = MovedPart().Match(rest);
        if (mm.Success)
        {
            moved = DateOnly.ParseExact(mm.Groups["d"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            rest = rest.Remove(mm.Index, mm.Length);
        }
        var um = UntilPart().Match(rest);
        if (um.Success)
        {
            until = DateOnly.ParseExact(um.Groups["d"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            rest = rest.Remove(um.Index, um.Length);
        }
        var struck = m.Groups["struck"].Success;
        if (struck && rest.EndsWith("~~", StringComparison.Ordinal)) rest = rest[..^2];
        return new MemoryEntry(date, m.Groups["src"].Value.Trim(), rest.Trim(), until, moved, struck);
    }

    public static string Format(MemoryEntry e)
    {
        var sb = new StringBuilder("- ");
        if (e.Struck) sb.Append("~~");
        sb.Append(Day(e.Date)).Append(Sep).Append(e.Source).Append(Sep).Append(e.Fact);
        if (e.Until is { } u) sb.Append(Sep).Append("until ").Append(Day(u));
        if (e.Struck) sb.Append("~~");
        if (e.Moved is { } m) sb.Append(Sep).Append("moved ").Append(Day(m));
        return sb.ToString();
    }

    /// <summary>Splits a file into entry blocks and fixed blocks, keeping order.</summary>
    public static List<MemoryBlock> Parse(string text)
    {
        var lines = SplitLines(text);
        var blocks = new List<MemoryBlock>();
        var i = 0;
        while (i < lines.Count)
        {
            var entry = ParseLine(lines[i]);
            if (entry is null)
            {
                blocks.Add(new MemoryBlock([lines[i]], blocks.Count));
                i++;
                continue;
            }
            var block = new List<string> { lines[i] };
            i++;
            while (i < lines.Count && lines[i].StartsWith("  ", StringComparison.Ordinal) && lines[i].Trim().Length > 0 && block.Count < MaxEntryLines)
            {
                block.Add(lines[i]);
                i++;
            }
            blocks.Add(new MemoryBlock(block, blocks.Count) { Entry = entry });
        }
        return blocks;
    }

    public static string Join(IEnumerable<MemoryBlock> blocks)
    {
        var text = string.Join('\n', blocks.Select(b => b.Text));
        return text.Length == 0 ? "" : text.TrimEnd('\n') + "\n";
    }

    public static List<string> SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    public static int LineCount(string text) => SplitLines(text).Count;

    public static int ByteCount(string text) => Encoding.UTF8.GetByteCount(text);

    /// <summary>
    /// "Exactly the same fact": the same text once spacing, a leading bullet and
    /// a final period are ignored. Nothing looser (case still counts).
    /// </summary>
    public static string Normalize(string text)
    {
        var t = Spaces().Replace(text, " ").Trim();
        if (t.StartsWith("- ", StringComparison.Ordinal) || t.StartsWith("* ", StringComparison.Ordinal)) t = t[2..].TrimStart();
        if (t.EndsWith('.')) t = t[..^1].TrimEnd();
        return t;
    }

    public static bool SameFact(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    /// <summary>Checks a new entry against the one-entry limits (1,000 characters, 20 lines).</summary>
    public static string? CheckSize(string fact)
    {
        if (string.IsNullOrWhiteSpace(fact)) return "The entry is empty.";
        if (fact.Length > MaxEntryChars) return $"The entry has {fact.Length} characters; the limit is {MaxEntryChars}.";
        if (SplitLines(fact).Count > MaxEntryLines) return $"The entry has more than {MaxEntryLines} lines.";
        return null;
    }

    /// <summary>Formats a new entry, indenting continuation lines so they stay with it.</summary>
    public static string NewEntry(DateOnly date, string source, string fact, DateOnly? until)
    {
        var lines = SplitLines(fact.Trim());
        var first = Format(new MemoryEntry(date, source, lines[0].Trim(), until, null, false));
        if (lines.Count == 1) return first;
        // The until marker must end the first line, so continuation lines follow it.
        return first + "\n" + string.Join('\n', lines.Skip(1).Select(l => "  " + l.Trim()));
    }
}
