using System.Text.RegularExpressions;

namespace OpenMono.Llm;

/// <summary>
/// Thrown when the provider rejects a request because it exceeds the model's context window
/// (a "context overflow"), as opposed to a transient network/server error. Retrying the same
/// oversized request can never succeed, so callers must shrink the payload (compact) before
/// retrying rather than blindly re-sending.
/// </summary>
public sealed class ContextOverflowException : Exception
{
    public ContextOverflowException(string message) : base(message) { }

    /// <summary>
    /// Classifies an error message (and, when available, HTTP status) as a context overflow.
    /// Matches the common phrasings llama.cpp and OpenAI-compatible relays use when the prompt
    /// does not fit the KV cache.
    /// </summary>
    public static bool IsOverflow(string? message, int? statusCode = null)
    {
        if (!string.IsNullOrWhiteSpace(message) && OverflowPatterns.IsMatch(message))
            return true;

        // 413 Request Entity Too Large is an unambiguous overflow signal on some gateways.
        return statusCode == 413;
    }

    private static readonly Regex OverflowPatterns = new(
        @"(context\s+length|maximum\s+context|context\s+window|exceeds?[^.\n]{0,40}(context|token|length)" +
        @"|prompt\s+(is\s+)?too\s+long|too\s+many\s+tokens|out\s+of\s+context|kv\s+cache\s+(full|overflow|exceed)" +
        @"|does\s+not\s+fit|insufficient\s+(context|space)|context\s+overflow)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
