using System.Text;

namespace OpenMono.Utils;

public static class InlineDiff
{
    private const int MaxTotalLines = 200;
    private const int ContextLines  = 2;

    public static string? FromEdit(string oldString, string newString, string filePath)
    {
        var old = SplitLines(oldString);
        var neu = SplitLines(newString);
        if (old.Length + neu.Length > MaxTotalLines) return null;

        var sb = new StringBuilder();
        sb.AppendLine($"--- {filePath}");
        sb.AppendLine($"+++ {filePath}");
        sb.AppendLine($"@@ -{1},{old.Length} +{1},{neu.Length} @@");
        foreach (var l in old) sb.AppendLine($"-{l}");
        foreach (var l in neu) sb.AppendLine($"+{l}");
        return sb.ToString().TrimEnd();
    }

    public static string? FromNewFile(string content, string filePath)
    {
        var lines = SplitLines(content);
        const int cap = 60;
        var sb = new StringBuilder();
        sb.AppendLine($"+++ {filePath}  (new file, {lines.Length} lines)");
        var show = Math.Min(lines.Length, cap);
        for (var i = 0; i < show; i++) sb.AppendLine($"+{lines[i]}");
        if (lines.Length > cap) sb.AppendLine($"  … {lines.Length - cap} more lines");
        return sb.ToString().TrimEnd();
    }

    public static string? FromOverwrite(string oldContent, string newContent, string filePath)
    {
        var a = SplitLines(oldContent);
        var b = SplitLines(newContent);
        if (a.Length + b.Length > MaxTotalLines) return null;

        var script = BuildEditScript(a, b);
        if (script.All(e => e.Kind == ' ')) return null;

        var hunks = BuildHunks(script, ContextLines);
        if (hunks.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine($"--- {filePath}");
        sb.AppendLine($"+++ {filePath}");

        var aLine = 0;
        var bLine = 0;
        foreach (var hunk in hunks)
        {
            var aStart = aLine + hunk.start + 1;
            var bStart = bLine + hunk.start + 1;
            var aCount = hunk.entries.Count(e => e.Kind is ' ' or '-');
            var bCount = hunk.entries.Count(e => e.Kind is ' ' or '+');
            sb.AppendLine($"@@ -{aStart},{aCount} +{bStart},{bCount} @@");
            foreach (var e in hunk.entries)
                sb.AppendLine($"{e.Kind}{e.Line}");
            aLine += aCount;
            bLine += bCount;
        }

        return sb.ToString().TrimEnd();
    }

    private static string[] SplitLines(string text)
        => text.Replace("\r\n", "\n").Split('\n');

    private record Entry(char Kind, string Line);

    private static List<Entry> BuildEditScript(string[] a, string[] b)
    {

        var m = a.Length;
        var n = b.Length;
        var dp = new int[m + 1, n + 1];
        for (var i = m - 1; i >= 0; i--)
            for (var j = n - 1; j >= 0; j--)
                dp[i, j] = a[i] == b[j]
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var result = new List<Entry>();
        var ai = 0; var bi = 0;
        while (ai < m || bi < n)
        {
            if (ai < m && bi < n && a[ai] == b[bi])
            { result.Add(new(' ', a[ai])); ai++; bi++; }
            else if (bi < n && (ai >= m || dp[ai, bi + 1] >= dp[ai + 1, bi]))
            { result.Add(new('+', b[bi])); bi++; }
            else
            { result.Add(new('-', a[ai])); ai++; }
        }
        return result;
    }

    private record Hunk(int start, List<Entry> entries);

    private static List<Hunk> BuildHunks(List<Entry> script, int ctx)
    {

        var changed = new HashSet<int>();
        for (var i = 0; i < script.Count; i++)
            if (script[i].Kind != ' ') changed.Add(i);

        if (changed.Count == 0) return [];

        var included = new SortedSet<int>();
        foreach (var idx in changed)
            for (var k = Math.Max(0, idx - ctx); k <= Math.Min(script.Count - 1, idx + ctx); k++)
                included.Add(k);

        var hunks = new List<Hunk>();
        var current = new List<Entry>();
        var start = -1;
        var prev = -1;

        foreach (var idx in included)
        {
            if (start == -1) { start = idx; }
            else if (idx > prev + 1) { hunks.Add(new(start, current)); current = []; start = idx; }
            current.Add(script[idx]);
            prev = idx;
        }
        if (current.Count > 0) hunks.Add(new(start, current));
        return hunks;
    }
}
