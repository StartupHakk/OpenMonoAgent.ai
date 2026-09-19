using System.Text;

namespace OpenMono.Decisions;

public static class ContextRenderer
{
    public const int MaxTaskChars = 2000;
    public const int MaxPathChars = 256;
    public const int MaxValueChars = 512;
    public const int MaxEvidenceChars = 2000;

    public static string RenderTask(string task, IReadOnlyDictionary<string, string?> contextFields)
    {
        var output = new StringBuilder();
        output.Append("TASK ");
        output.AppendLine(TruncateUtf8Safe(task, MaxTaskChars));
        foreach (var (key, value) in contextFields)
        {
            output.Append("CONTEXT ");
            output.Append(TruncateUtf8Safe(key, MaxPathChars));
            output.Append('=');
            output.AppendLine(TruncateUtf8Safe(value ?? string.Empty, LimitFor(key)));
        }
        return output.ToString();
    }

    public static string RenderElement(string kind, string label, string current)
    {
        return string.Concat(
            "ELEMENT ",
            TruncateUtf8Safe(kind, MaxPathChars),
            " \"",
            TruncateUtf8Safe(label, MaxValueChars),
            "\" value=\"",
            TruncateUtf8Safe(current, MaxValueChars),
            '"');
    }

    private static int LimitFor(string key)
    {
        var lowered = key.ToLowerInvariant();
        if (lowered.Contains("path", StringComparison.Ordinal) || lowered.Contains("file", StringComparison.Ordinal))
            return MaxPathChars;
        if (lowered.Contains("evidence", StringComparison.Ordinal) || lowered.Contains("diff", StringComparison.Ordinal))
            return MaxEvidenceChars;
        return MaxValueChars;
    }

    internal static string TruncateUtf8Safe(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return text;
        var candidate = text[..maxChars];
        if (char.IsHighSurrogate(candidate[^1]) && char.IsLowSurrogate(text[maxChars]))
            candidate = candidate[..^1];
        var bytes = Encoding.UTF8.GetBytes(candidate);
        var end = bytes.Length;
        while (end > 0 && (bytes[end - 1] & 0xC0) == 0x80)
            end--;
        var trailing = bytes.Length - end;
        if (trailing == 0)
            return candidate;
        if (end == 0)
            return string.Empty;
        var lead = bytes[end - 1];
        var expected = (lead & 0xE0) == 0xC0 ? 1 : (lead & 0xF0) == 0xE0 ? 2 : (lead & 0xF8) == 0xF0 ? 3 : -1;
        return expected == trailing ? candidate : Encoding.UTF8.GetString(bytes, 0, end - 1);
    }
}
