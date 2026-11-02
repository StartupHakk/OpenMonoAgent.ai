using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenMono.Llm;
using OpenMono.Utils;

namespace OpenMono.Session;

public sealed class Compactor
{
    private readonly ILlmClient _llm;
    private readonly Func<int> _contextSize;

    private static readonly HashSet<string> FileToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "FileRead", "FileEdit", "FileWrite", "Read", "Edit", "Write",
    };

    /// <summary>
    /// Fraction of the context window reserved for the verbatim "recent turns" keep-window.
    /// The keep-window is capped in TOKENS, not turn count: a handful of user turns can each
    /// carry tens of thousands of tokens of tool output, so "keep the last 4 turns" alone can
    /// exceed the whole window and make compaction structurally unable to shrink anything.
    /// </summary>
    private const double KeepWindowFraction = 0.30;
    private const int KeepWindowMinTokens = 512;
    // Fraction of the window a single summarization prompt may occupy (leaves room for the
    // summary's own output tokens inside the same window).
    private const double SummaryBudgetFraction = 0.70;

    public Compactor(ILlmClient llm, int contextSize)
        : this(llm, () => contextSize)
    {
    }

    public Compactor(ILlmClient llm, Func<int> contextSize)
    {
        _llm = llm;
        _contextSize = contextSize;
    }

    public int CurrentContextSize => _contextSize();

    public bool NeedsCompaction(SessionState session, int lastPromptTokens = 0)
        => NeedsCompaction(session.Messages, lastPromptTokens);

    public bool NeedsCompaction(IReadOnlyList<Message> effectiveMessages, int lastPromptTokens = 0)
    {
        if (!HasCompactableContent(effectiveMessages))
            return false;

        var tokens = lastPromptTokens > 0 ? lastPromptTokens : TokenEstimator.EstimateMessages(effectiveMessages);
        var threshold = (int)(_contextSize() * 0.80);
        return tokens > threshold;
    }

    public bool HasCompactableContent(IReadOnlyList<Message> messages)
    {
        var systemMessages = messages.Where(m => m.Role == MessageRole.System).ToList();
        var (recent, spilled) = GetRecentTurns(messages, keepTurns: 4);
        return messages.Except(systemMessages).Except(recent).Count() >= 4
            || spilled.Count >= 4;
    }

    public async Task<(SessionState Session, CompactionReport Report)> CompactAsync(
        SessionState session,
        string? customInstructions = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var messagesBefore = session.Messages.Count;
        var tokensBefore = TokenEstimator.EstimateMessages(session.Messages);

        var systemMessages = session.Messages.Where(m => m.Role == MessageRole.System).ToList();
        var (recentTurns, recentSpilled) = GetRecentTurns(session.Messages, keepTurns: 4);
        // recentSpilled = older part of the "recent" window that exceeded the size cap. It is
        // not verbatim-kept, so it goes into the summarized portion like everything else —
        // nothing is ever dropped, only summarized.
        var toSummarize = session.Messages
            .Except(systemMessages)
            .Except(recentTurns)
            .Concat(recentSpilled)
            .ToList();

        if (toSummarize.Count < 4)
        {
            sw.Stop();
            return (session, EmptyReport(messagesBefore, tokensBefore, sw.Elapsed));
        }

        var compressedByRole = toSummarize
            .GroupBy(m => m.Role)
            .ToDictionary(g => g.Key, g => g.Count());

        var compressedToolCalls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var filesTouched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var msg in toSummarize)
        {
            if (msg.ToolCalls is not null)
            {
                foreach (var call in msg.ToolCalls)
                {
                    compressedToolCalls.TryGetValue(call.Name, out var count);
                    compressedToolCalls[call.Name] = count + 1;

                    if (FileToolNames.Contains(call.Name))
                    {
                        var path = TryExtractFilePath(call.Arguments);
                        if (path is not null) filesTouched.Add(path);
                    }
                }
            }
        }

        var summary = await GenerateSummaryAsync(toSummarize, customInstructions, ct);
        var formatted = SummaryPrompt.FormatSummary(summary);

        var compacted = new SessionState();
        foreach (var msg in systemMessages)
            compacted.AddMessage(msg);

        compacted.AddMessage(new Message
        {
            Role = MessageRole.User,
            Content = $"[Conversation summary — {toSummarize.Count} messages compacted]\n\n{formatted}",
        });

        compacted.AddMessage(new Message
        {
            Role = MessageRole.Assistant,
            Content = "Understood. I have the context from the summarized conversation. Continuing from where we left off.",
        });

        foreach (var msg in recentTurns)
            compacted.AddMessage(msg);

        compacted.TotalTokensUsed = session.TotalTokensUsed;
        compacted.TurnCount = session.TurnCount;

        sw.Stop();
        var tokensAfter = TokenEstimator.EstimateMessages(compacted.Messages);

        var report = new CompactionReport
        {
            MessagesBefore = messagesBefore,
            MessagesAfter = compacted.Messages.Count,
            MessagesCompressed = toSummarize.Count,
            TokensBefore = tokensBefore,
            TokensAfter = tokensAfter,
            CompressedByRole = compressedByRole,
            CompressedToolCalls = compressedToolCalls,
            FilesTouched = filesTouched.OrderBy(p => p).ToList(),
            ToolOutputsEvicted = 0,
            EvictedBytes = 0,
            Duration = sw.Elapsed,
            ContextWindowSize = _contextSize(),
            SummaryText = formatted,
        };

        return (compacted, report);
    }

    // --- Summarization (chunked) -----------------------------------------------------------

    private async Task<string> GenerateSummaryAsync(
        List<Message> messages,
        string? customInstructions,
        CancellationToken ct)
    {
        var budget = (int)(_contextSize() * SummaryBudgetFraction);
        var chunks = ChunkForSummarization(messages, budget);
        if (chunks.Count == 0)
            return "";

        var basePrompt = SummaryPrompt.BuildPrompt(customInstructions);
        var partials = new List<string>();
        for (var i = 0; i < chunks.Count; i++)
        {
            var header = chunks.Count > 1
                ? $"Part {i + 1} of {chunks.Count} of the conversation to summarize. " +
                  "Other parts are summarized separately and merged afterwards — " +
                  "cover everything in this part thoroughly; do not try to account for the other parts.\n\n"
                : null;
            var text = await SummarizeChunkAsync(basePrompt, header, chunks[i], ct);
            if (!string.IsNullOrWhiteSpace(text))
                partials.Add(text);
        }

        return await RollUpSummariesAsync(partials, basePrompt, ct);
    }

    private async Task<string> SummarizeChunkAsync(
        string basePrompt,
        string? header,
        List<Message> messages,
        CancellationToken ct)
    {
        var conversationText = BuildConversationText(messages);
        var userContent = header is null ? conversationText : header + conversationText;

        var summaryMessages = new List<Message>
        {
            new() { Role = MessageRole.System, Content = basePrompt },
            new() { Role = MessageRole.User, Content = userContent },
        };

        SummarySafety.EnsureSummaryFits(summaryMessages, _contextSize());

        var sb = new StringBuilder();
        var options = new LlmOptions { MaxTokens = 4096, Temperature = 0.1, EnableThinking = false };

        await foreach (var chunk in _llm.StreamChatAsync(summaryMessages, tools: null, options, ct))
        {
            if (chunk.TextDelta is not null)
                sb.Append(chunk.TextDelta);
        }

        return sb.ToString();
    }

    private async Task<string> RollUpSummariesAsync(
        List<string> partials,
        string basePrompt,
        CancellationToken ct)
    {
        if (partials.Count == 0) return "";
        if (partials.Count == 1) return partials[0];

        var combined = string.Join("\n\n", partials.Select((s, i) => $"--- Part {i + 1} ---\n{s}"));

        var rollupMessages = new List<Message>
        {
            new() { Role = MessageRole.System, Content = basePrompt },
            new() { Role = MessageRole.User, Content =
                $"The conversation was summarized in {partials.Count} parts. Merge them into ONE " +
                "coherent summary following the required structure, preserving all concrete details " +
                "(file names, code, decisions, user messages). Do not lose information by merging.\n\n" + combined },
        };

        // The merge input is the partials' TEXT, not the raw conversation — far smaller. If even
        // that cannot fit (pathologically small window), concatenate deterministically rather than
        // dropping parts.
        if (TokenEstimate.EstimatePayload(rollupMessages) > (int)(_contextSize() * 0.80))
            return combined;

        var sb = new StringBuilder();
        var options = new LlmOptions { MaxTokens = 4096, Temperature = 0.1, EnableThinking = false };
        await foreach (var chunk in _llm.StreamChatAsync(rollupMessages, tools: null, options, ct))
        {
            if (chunk.TextDelta is not null)
                sb.Append(chunk.TextDelta);
        }
        return sb.ToString();
    }

    // Splits messages into the fewest chunks whose serialized payload fits under the summary
    // budget. A single message larger than the budget (e.g. a huge tool output) is truncated
    // — with a note — so it is summarized rather than silently dropped.
    private List<List<Message>> ChunkForSummarization(List<Message> messages, int budgetTokens)
    {
        var overhead = (int)(TokenEstimate.EstimatePayload(new List<Message>
        {
            new() { Role = MessageRole.System, Content = SummaryPrompt.BuildPrompt() },
            new() { Role = MessageRole.User, Content = "" },
        }) * 1.20); // headroom for JSON-escaping differences between estimator and real payload
        var capacity = Math.Max(budgetTokens - overhead, 512);

        var chunks = new List<List<Message>>();
        var current = new List<Message>();
        var currentTokens = 0;
        foreach (var msg in messages)
        {
            // Size with the SAME escaped-payload estimator that EnsureSummaryFits validates with.
            // The raw TokenEstimator (chars/4) undercounts quote/code-heavy content after JSON
            // escaping, so a chunk it deems "fits" can still overflow the safety check — the cause
            // of resume-crashing ContextOverflowExceptions.
            var t = EstimateEscapedMessage(msg);
            if (current.Count > 0 && currentTokens + t > capacity)
            {
                chunks.Add(current);
                current = new List<Message>();
                currentTokens = 0;
            }
            if (t > capacity)
            {
                // One message bigger than the whole chunk budget (e.g. a huge tool output):
                // truncate it — with a note — so it is summarized rather than silently dropped.
                var truncated = TruncateToFit(msg, capacity);
                current.Add(truncated);
                currentTokens = EstimateEscapedMessage(truncated);
                continue;
            }
            current.Add(msg);
            currentTokens += t;
        }
        if (current.Count > 0)
            chunks.Add(current);
        return chunks;
    }

    private static Message TruncateToFit(Message msg, int maxTokens)
    {
        // Cap in ESCAPED-token space (what EnsureSummaryFits measures), not raw chars. A raw-char
        // cap lets quote/code-heavy content escape to well over maxTokens after JSON-escaping.
        string? content = msg.Content;
        if (content is not null && TokenEstimate.EstimateEscapedText(content) > maxTokens)
            content = TruncateEscaped(content, Math.Max(256, maxTokens - 24))
                      + $"\n[... truncated — {content.Length} chars total]";

        List<ToolCall>? calls = null;
        if (msg.ToolCalls is { Count: > 0 })
        {
            calls = new List<ToolCall>(msg.ToolCalls.Count);
            foreach (var tc in msg.ToolCalls)
            {
                string args = tc.Arguments;
                if (TokenEstimate.EstimateEscapedText(args) > maxTokens)
                    args = TruncateEscaped(args, Math.Max(256, maxTokens - 16)) + "[... truncated]";
                calls.Add(tc with { Arguments = args });
            }
        }

        List<ToolCall> callsToApply = calls ?? [];
        return msg with { Content = content, ToolCalls = callsToApply };
    }

    /// <summary>
    /// Truncates <paramref name="text"/> to the longest prefix whose JSON-escaped size fits
    /// under <paramref name="maxTokens"/> tokens. Escaping inflates differently per character —
    /// a plain char stays 1 escaped char, but HTML-sensitive ones (a double quote, <, >, &, ')
    /// expand to a 6-char \uXXXX sequence — so the cut point is found by binary search over the
    /// escaped size, never by assuming a fixed chars-per-token ratio (the old raw-length cut
    /// left quote-heavy tool outputs untruncated and overflowing).
    /// </summary>
    private static string TruncateEscaped(string text, int maxTokens)
    {
        if (TokenEstimate.EstimateEscapedText(text) <= maxTokens)
            return text;

        var lo = 0;
        var hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (TokenEstimate.EstimateEscapedText(text[..mid]) <= maxTokens)
                lo = mid;
            else
                hi = mid - 1;
        }
        return lo == 0 ? text[..Math.Min(text.Length, 16)] : text[..lo];
    }

    /// <summary>Estimates a message's escaped-payload token cost (see <see cref="ChunkForSummarization"/>).</summary>
    private static int EstimateEscapedMessage(Message m)
    {
        var cost = 20; // per-message overhead, matches TokenEstimator.MessageCharCount
        if (m.Content is not null)
            cost += TokenEstimate.EstimateEscapedText(m.Content);
        if (m.ToolCalls is { Count: > 0 })
            foreach (var c in m.ToolCalls)
                cost += TokenEstimate.EstimateEscapedText(c.Arguments ?? "");
        return cost;
    }

    // --- Keep-window (size-capped) ---------------------------------------------------------

    /// <summary>
    /// Splits history into the verbatim keep-window and the part that must be summarized.
    /// The keep-window is the most recent <paramref name="keepTurns"/> user turns, size-capped:
    /// user/assistant conversation is always kept verbatim (it is the working context and is
    /// small), but tool outputs inside the window that push it over the token budget are
    /// moved — oldest first — into the returned <c>Spilled</c> list, where they get summarized
    /// (never dropped). A pending tool call awaiting a permission decision is never moved.
    /// </summary>
    private (List<Message> Recent, List<Message> Spilled) GetRecentTurns(IReadOnlyList<Message> messages, int keepTurns)
    {
        var nonSystem = messages.Where(m => m.Role != MessageRole.System).ToList();
        var turns = 0;
        var startIndex = nonSystem.Count;

        for (var i = nonSystem.Count - 1; i >= 0 && turns < keepTurns; i--)
        {
            startIndex = i;
            if (nonSystem[i].Role == MessageRole.User)
                turns++;
        }

        // A pending tool call awaiting a permission decision can outlive several user turns
        // (queued permissions). Never let it fall into the summarized portion — resolving it
        // later needs to find this exact message still in history.
        var pendingCallIds = FindUnansweredToolCallIds(nonSystem);
        var pendingIndex = FindEarliestUnansweredToolCallIndex(nonSystem);
        if (pendingIndex is int p && p < startIndex)
            startIndex = p;

        var recent = nonSystem.Skip(startIndex).ToList();
        var budget = Math.Max((int)(_contextSize() * KeepWindowFraction), KeepWindowMinTokens);
        if (TokenEstimator.EstimateMessages(recent) <= budget)
            return (recent, []);

        // The recent turns exceed the token budget (huge tool outputs). Move tool messages out
        // of the keep-window, oldest first, until it fits — but never a tool result that
        // belongs to a still-unanswered (pending) tool call.
        var spilled = new List<Message>();
        while (TokenEstimator.EstimateMessages(recent) > budget)
        {
            var movable = -1;
            for (var j = 0; j < recent.Count; j++)
            {
                var id = recent[j].ToolCallId;
                if (recent[j].Role == MessageRole.Tool
                    && (id is null || !pendingCallIds.Contains(id)))
                {
                    movable = j;
                    break;
                }
            }
            if (movable < 0)
                break; // only protected (pending) results left — nothing more can move
            spilled.Add(recent[movable]);
            recent.RemoveAt(movable);
        }

        return (recent, spilled);
    }

    private static HashSet<string> FindUnansweredToolCallIds(List<Message> nonSystem)
    {
        var answered = nonSystem
            .Where(m => m.Role == MessageRole.Tool && m.ToolCallId is not null)
            .Select(m => m.ToolCallId!)
            .ToHashSet();

        var pending = new HashSet<string>();
        foreach (var m in nonSystem)
        {
            if (m.Role == MessageRole.Assistant && m.ToolCalls is { Count: > 0 } calls)
            {
                foreach (var c in calls)
                    if (!answered.Contains(c.Id))
                        pending.Add(c.Id);
            }
        }
        return pending;
    }

    private static int? FindEarliestUnansweredToolCallIndex(List<Message> nonSystem)
    {
        var answered = nonSystem
            .Where(m => m.Role == MessageRole.Tool && m.ToolCallId is not null)
            .Select(m => m.ToolCallId!)
            .ToHashSet();

        for (var i = 0; i < nonSystem.Count; i++)
        {
            if (nonSystem[i].Role == MessageRole.Assistant
                && nonSystem[i].ToolCalls is { Count: > 0 } calls
                && calls.Any(c => !answered.Contains(c.Id)))
                return i;
        }
        return null;
    }

    // --- Helpers ---------------------------------------------------------------------------

    private static string BuildConversationText(List<Message> messages)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            var role = msg.Role.ToString().ToUpperInvariant();
            var content = msg.Content ?? "(tool call/result)";
            sb.AppendLine($"[{role}]: {content}\n");
        }
        return sb.ToString();
    }

    private static string? TryExtractFilePath(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            foreach (var key in new[] { "file_path", "path", "filePath", "filename" })
            {
                if (doc.RootElement.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
        }
        catch (JsonException) { }
        return null;
    }

    internal static int EstimateTokens(IReadOnlyList<Message> messages)
        => TokenEstimate.EstimatePayload(messages);

    private CompactionReport EmptyReport(int messagesBefore, int tokensBefore, TimeSpan duration) =>
        new()
        {
            MessagesBefore = messagesBefore,
            MessagesAfter = messagesBefore,
            MessagesCompressed = 0,
            TokensBefore = tokensBefore,
            TokensAfter = tokensBefore,
            CompressedByRole = new(),
            CompressedToolCalls = new(),
            FilesTouched = new(),
            ToolOutputsEvicted = 0,
            EvictedBytes = 0,
            Duration = duration,
            ContextWindowSize = _contextSize(),
        };
}
