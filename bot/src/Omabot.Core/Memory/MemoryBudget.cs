// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;

namespace Omabot.Memory;

public sealed record MemoryLimits(int MaxLines = 200, int MaxBytes = 24 * 1024)
{
    public bool Fits(string text) => MemoryEntries.LineCount(text) <= MaxLines && MemoryEntries.ByteCount(text) <= MaxBytes;
}

/// <summary>What goes in the prompt at turn start.</summary>
public sealed record LoadedMemory(string Block, bool Cut, int LinesLoaded, int ExpiredSkipped);

/// <summary>Result of fitting a file into the budget by moving entries to the archive.</summary>
public sealed record FitResult(bool Fits, string Text, IReadOnlyList<string> Moved);

public static class MemoryBudget
{
    public const string CutNotice = "(MEMORY.md was cut here to fit the load budget; the rest did not load. Use memory_read_topic or tidy the file.)";

    /// <summary>
    /// The first MaxLines lines or MaxBytes bytes of MEMORY.md, whichever cuts
    /// first, with expired entries left out (UNIT-MEM-01, 02, 08).
    /// </summary>
    public static LoadedMemory Load(string text, DateOnly today, MemoryLimits limits)
    {
        var blocks = MemoryEntries.Parse(text);
        var expired = 0;
        var lines = new List<string>();
        foreach (var b in blocks)
        {
            if (b.Entry is { } e && e.ExpiredOn(today))
            {
                expired++;
                continue;
            }
            lines.AddRange(b.Lines);
        }
        var sb = new StringBuilder();
        var bytes = 0;
        var count = 0;
        var cut = false;
        foreach (var line in lines)
        {
            var size = Encoding.UTF8.GetByteCount(line) + 1;
            if (count + 1 > limits.MaxLines || bytes + size > limits.MaxBytes)
            {
                cut = true;
                break;
            }
            sb.Append(line).Append('\n');
            bytes += size;
            count++;
        }
        var block = sb.ToString().TrimEnd('\n');
        if (block.Trim().Length == 0 && !cut) block = "(no memory yet)";
        if (cut) block += "\n" + CutNotice;
        return new LoadedMemory(block, cut, count, expired);
    }

    /// <summary>
    /// Moves dated entries out until the text fits: struck first, then
    /// expired, then the oldest live entries, each marked "· moved DATE".
    /// Undated lines and health or safety facts never move (UNIT-MEM-03, 04).
    /// Returns the remaining text and the archived lines in move order.
    /// </summary>
    public static FitResult Fit(string text, DateOnly today, MemoryLimits limits)
    {
        if (limits.Fits(text)) return new FitResult(true, text, []);
        var blocks = MemoryEntries.Parse(text);
        var movable = blocks
            .Where(b => b.Entry is { } e && !e.IsSafety)
            .OrderBy(b => b.Entry!.Struck ? 0 : b.Entry!.ExpiredOn(today) ? 1 : 2)
            .ThenBy(b => b.Entry!.Date)
            .ThenBy(b => b.Order)
            .ToList();
        var kept = blocks.ToList();
        var moved = new List<string>();
        foreach (var b in movable)
        {
            kept.Remove(b);
            moved.Add(MarkMoved(b, today));
            if (limits.Fits(MemoryEntries.Join(kept))) return new FitResult(true, MemoryEntries.Join(kept), moved);
        }
        return new FitResult(false, MemoryEntries.Join(kept), moved);
    }

    public static string MarkMoved(MemoryBlock b, DateOnly today)
    {
        var e = b.Entry!;
        var first = MemoryEntries.Format(e with { Moved = today });
        return b.Lines.Count == 1 ? first : first + "\n" + string.Join('\n', b.Lines.Skip(1));
    }
}
