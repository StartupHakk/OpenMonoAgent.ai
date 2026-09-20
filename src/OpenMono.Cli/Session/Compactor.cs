using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenMono.Decisions;
using OpenMono.Llm;
using OpenMono.Utils;

namespace OpenMono.Session;

/// <summary>
/// Code-owned knobs for the Phase 4 verbatim compaction pass. The model
/// never sees or sets these: relevance scores come from the active
/// <see cref="IDecisionBackend"/>, thresholds live here.
/// </summary>
public sealed record VerbatimCompactionOptions(
    /// <summary>Master switch. False = straight to the summarizing compactor.</summary>
    bool Enabled = true,
    /// <summary>
    /// Conservative keep threshold (default 0.5). A (tool-call, result)
    /// pair is dropped only when relevance falls below this; anything at
    /// or above is kept (verbatim or truncated). When in doubt the pass
    /// keeps — over-dropping context hurts the agent.
    /// </summary>
    double KeepThreshold = 0.5,
    /// <summary>First-N chars retained when a relevant pair is not verbatim-worthy.</summary>
    int TruncateChars = 2000,
    /// <summary>
    /// Minimum token-reduction fraction for the verbatim pass to count.
    /// Below this the pass falls back to the existing summarizing
    /// compactor, which can always compress further.
    /// </summary>
    double ReductionFloor = 0.15);

public sealed class Compactor
{
    private const string KeepVerbatimProposition =
        "This tool result contains exact content that must be kept verbatim, " +
        "such as file paths, error messages, or exact commands.";

    private readonly ILlmClient _llm;
    private readonly int _contextSize;
    private readonly IDecisionBackend _backend;
    private readonly VerbatimCompactionOptions _verbatim;

    private static readonly HashSet<string> FileToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "FileRead", "FileEdit", "FileWrite", "Read", "Edit", "Write",
    };

    public Compactor(
        ILlmClient llm,
        int contextSize,
        IDecisionBackend? backend = null,
        VerbatimCompactionOptions? verbatimOptions = null)
    {
        _llm = llm;
        _contextSize = contextSize;
        _backend = backend ?? new HeuristicBackend(
            new DecisionOptions(false, 0.85, 0.6, 0.5, 64));
        _verbatim = verbatimOptions ?? new VerbatimCompactionOptions();
    }

    public bool NeedsCompaction(SessionState session, int lastPromptTokens = 0)
        => NeedsCompaction(session.Messages, lastPromptTokens);

    public bool NeedsCompaction(IReadOnlyList<Message> effectiveMessages, int lastPromptTokens = 0)
    {
        if (!HasCompactableContent(effectiveMessages))
            return false;

        var tokens = lastPromptTokens > 0 ? lastPromptTokens : TokenEstimator.EstimateMessages(effectiveMessages);
        var threshold = (int)(_contextSize * 0.80);
        return tokens > threshold;
    }

    public static bool HasCompactableContent(IReadOnlyList<Message> messages)
    {
        var systemMessages = messages.Where(m => m.Role == MessageRole.System).ToList();
        var recentTurns = GetRecentTurns(messages, keepTurns: 4);
        return messages.Except(systemMessages).Except(recentTurns).Count() >= 4;
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
        var recentTurns = GetRecentTurns(session.Messages, keepTurns: 4);
        var toSummarize = session.Messages
            .Except(systemMessages)
            .Except(recentTurns)
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

        var (evictedMessages, evictedCount, evictedBytes) = SummarySafety.EvictLargeToolOutputs(toSummarize);

        var summary = await GenerateSummaryAsync(evictedMessages, customInstructions, ct);
        var formatted = SummaryPrompt.FormatSummary(summary);

        var compacted = new SessionState();
        foreach (var msg in systemMessages)
            compacted.AddMessage(msg);

        compacted.AddMessage(new Message
        {
            Role = MessageRole.User,
            Content = $"[Conversation summary — {toSummarize.Count} messages compacted, {evictedCount} large tool outputs evicted]\n\n{formatted}",
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
            ToolOutputsEvicted = evictedCount,
            EvictedBytes = evictedBytes,
            Duration = sw.Elapsed,
            ContextWindowSize = _contextSize,
            SummaryText = formatted,
        };

        return (compacted, report);
    }

    /// <summary>
    /// Phase 4 verbatim-first compaction. Tries the relevance pass over
    /// (tool-call, result) pairs via the active <see cref="IDecisionBackend"/>
    /// and keeps that result when it reduces enough; otherwise falls back
    /// to the summarizing <see cref="CompactAsync"/>. User/assistant text
    /// is always kept verbatim and in order; only tool pairs are ever
    /// dropped or truncated. Works on the heuristic backend today — the
    /// two questions are relevance scores, never LLM generation.
    /// </summary>
    public async Task<(SessionState Session, CompactionReport Report)> CompactWithVerbatimFirstAsync(
        SessionState session,
        string? customInstructions = null,
        CancellationToken ct = default)
    {
        if (_verbatim.Enabled && TryCompactVerbatim(session, ct) is { } attempt && attempt.Sufficient)
            return (attempt.Session, attempt.Report);
        return await CompactAsync(session, customInstructions, ct);
    }

    private sealed record VerbatimAttempt(SessionState Session, CompactionReport Report, bool Sufficient);

    private VerbatimAttempt? TryCompactVerbatim(SessionState session, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var messagesBefore = session.Messages.Count;
        var tokensBefore = TokenEstimator.EstimateMessages(session.Messages);

        var systemMessages = session.Messages.Where(m => m.Role == MessageRole.System).ToList();
        var recentTurns = GetRecentTurns(session.Messages, keepTurns: 4);
        var toSummarize = session.Messages
            .Except(systemMessages)
            .Except(recentTurns)
            .ToList();

        if (toSummarize.Count < 4)
        {
            sw.Stop();
            return null;
        }

        var contextQuery = string.Join("\n",
            recentTurns.Select(m => m.Content).Where(c => !string.IsNullOrWhiteSpace(c)));

        // Walk the window in original order so the rebuilt history keeps it.
        // Only forward tool results pair with a call; anything else is kept.
        var consumed = new bool[toSummarize.Count];
        var kept = new List<Message>();
        var dropped = 0;
        var truncated = 0;

        for (var i = 0; i < toSummarize.Count; i++)
        {
            if (consumed[i])
                continue;
            var msg = toSummarize[i];
            if (msg.Role == MessageRole.Assistant && msg.ToolCalls is { Count: > 0 } calls)
            {
                var resultIdx = new List<int>();
                foreach (var c in calls)
                    for (var j = i + 1; j < toSummarize.Count; j++)
                        if (!consumed[j]
                            && toSummarize[j].Role == MessageRole.Tool
                            && string.Equals(toSummarize[j].ToolCallId, c.Id, StringComparison.Ordinal)
                            && !resultIdx.Contains(j))
                            resultIdx.Add(j);
                if (resultIdx.Count == 0)
                {
                    // Unanswered call (pending permission, or the result fell
                    // outside the window): never drop — resolving it later
                    // needs this exact message still in history.
                    kept.Add(msg);
                    continue;
                }
                ct.ThrowIfCancellationRequested();
                var pairText = BuildPairText(msg, resultIdx.Select(j => toSummarize[j]).ToList());
                // No current-context signal means no evidence of staleness:
                // keep rather than drop on an empty query.
                var relevance = string.IsNullOrWhiteSpace(contextQuery)
                    ? 1.0
                    : _backend.Relevance(contextQuery, pairText, ct);
                if (relevance < _verbatim.KeepThreshold)
                {
                    consumed[i] = true;
                    foreach (var j in resultIdx)
                        consumed[j] = true;
                    dropped += 1 + resultIdx.Count;
                    continue;
                }
                var verbatimScore = _backend.JudgeTrue(pairText, KeepVerbatimProposition, ct);
                consumed[i] = true;
                kept.Add(msg);
                foreach (var j in resultIdx)
                {
                    consumed[j] = true;
                    var result = toSummarize[j];
                    if (verbatimScore >= _verbatim.KeepThreshold)
                    {
                        kept.Add(result);
                    }
                    else
                    {
                        kept.Add(TruncateResult(result));
                        truncated++;
                    }
                }
            }
            else
            {
                // User/assistant text and orphan tool results (call outside
                // the window): conservative keep, verbatim, in order.
                kept.Add(msg);
            }
        }

        var compacted = new SessionState();
        foreach (var msg in systemMessages)
            compacted.AddMessage(msg);
        foreach (var msg in kept)
            compacted.AddMessage(msg);
        foreach (var msg in recentTurns)
            compacted.AddMessage(msg);

        compacted.TotalTokensUsed = session.TotalTokensUsed;
        compacted.TurnCount = session.TurnCount;

        sw.Stop();
        var tokensAfter = TokenEstimator.EstimateMessages(compacted.Messages);
        var reduction = tokensBefore > 0 ? 1.0 - (double)tokensAfter / tokensBefore : 0;

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
            ToolOutputsEvicted = dropped,
            EvictedBytes = 0,
            Duration = sw.Elapsed,
            ContextWindowSize = _contextSize,
            Strategy = "verbatim",
            VerbatimDropped = dropped,
            VerbatimTruncated = truncated,
        };

        return new VerbatimAttempt(compacted, report, reduction >= _verbatim.ReductionFloor);
    }

    private Message TruncateResult(Message result)
    {
        if (string.IsNullOrEmpty(result.Content) || result.Content.Length <= _verbatim.TruncateChars)
            return result;
        return result with
        {
            Content = result.Content[.._verbatim.TruncateChars] +
                $"\n…[truncated by verbatim compactor: kept first {_verbatim.TruncateChars} of {result.Content.Length} chars]",
        };
    }

    private static string BuildPairText(Message call, IReadOnlyList<Message> results)
    {
        var sb = new StringBuilder();
        foreach (var c in call.ToolCalls ?? Enumerable.Empty<ToolCall>())
            sb.AppendLine($"tool {c.Name} {c.Arguments}");
        foreach (var r in results)
            sb.AppendLine($"result: {r.Content}");
        return sb.ToString();
    }

    private async Task<string> GenerateSummaryAsync(
        List<Message> messages,
        string? customInstructions,
        CancellationToken ct)
    {
        var conversationText = BuildConversationText(messages);

        var summaryMessages = new List<Message>
        {
            new() { Role = MessageRole.System, Content = SummaryPrompt.BuildPrompt(customInstructions) },
            new() { Role = MessageRole.User, Content = conversationText },
        };

        SummarySafety.EnsureSummaryFits(summaryMessages, _contextSize);

        var sb = new StringBuilder();
        var options = new LlmOptions { MaxTokens = 4096, Temperature = 0.1, EnableThinking = false };

        await foreach (var chunk in _llm.StreamChatAsync(summaryMessages, tools: null, options, ct))
        {
            if (chunk.TextDelta is not null)
                sb.Append(chunk.TextDelta);
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

    private static List<Message> GetRecentTurns(IReadOnlyList<Message> messages, int keepTurns)
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
        var pendingIndex = FindEarliestUnansweredToolCallIndex(nonSystem);
        if (pendingIndex is int p && p < startIndex)
            startIndex = p;

        return nonSystem.Skip(startIndex).ToList();
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
            ContextWindowSize = _contextSize,
        };
}
