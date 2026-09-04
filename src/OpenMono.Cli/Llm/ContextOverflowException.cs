using System.Text.RegularExpressions;

namespace OpenMono.Llm;

public sealed class ContextOverflowException : Exception
{
    public ContextOverflowException(string message) : base(message) { }

    public static bool IsOverflow(string? message, int? statusCode = null)
    {
        if (!string.IsNullOrWhiteSpace(message) && OverflowPatterns.IsMatch(message))
            return true;

        return statusCode == 413;
    }

    private static readonly Regex OverflowPatterns = new(
        @"(context\s+length|maximum\s+context|context\s+window|exceeds?[^.\n]{0,40}(context|token|length)" +
        @"|prompt\s+(is\s+)?too\s+long|too\s+many\s+tokens|out\s+of\s+context|kv\s+cache\s+(full|overflow|exceed)" +
        @"|does\s+not\s+fit|insufficient\s+(context|space)|context\s+overflow)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
