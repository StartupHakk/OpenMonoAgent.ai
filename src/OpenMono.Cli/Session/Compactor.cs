using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenMono.Llm;

namespace OpenMono.Session;

public sealed class Compactor
{
    private readonly ILlmClient _llm;
    private readonly int _contextSize;
    private const int LargeToolOutputThreshold = 2000;

    private static readonly HashSet<string> FileToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "FileRead", "FileEdit", "FileWrite", "Read", "Edit", "Write",
    };

    public Compactor(ILlmClient llm, int contextSize)
    {
        _llm = llm;
        _contextSize = contextSize;
    }

    public bool NeedsCompaction(SessionState session, int lastPromptTokens = 0)
        => NeedsCompaction(session.Messages, lastPromptTokens);

    public bool NeedsCompaction(IReadOnlyList<Message> effectiveMessages, int lastPromptTokens = 0)
    {
        var tokens = lastPromptTokens > 0 ? lastPromptTokens : EstimateTokens(effectiveMessages);
        var threshold = (int)(_contextSize * 0.80);
        return tokens > threshold;
    }

    public async Task<(SessionState Session, CompactionReport Report)> CompactAsync(
        SessionState session,
        string? customInstructions = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var messagesBefore = session.Messages.Count;
        var tokensBefore = EstimateTokens(session.Messages);

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

        var (evictedMessages, evictedCount, evictedBytes) = EvictLargeToolOutputs(toSummarize);

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
        var tokensAfter = EstimateTokens(compacted.Messages);

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
        };

        return (compacted, report);
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

        var sb = new StringBuilder();
        var options = new LlmOptions { MaxTokens = 4096, Temperature = 0.1 };

        await foreach (var chunk in _llm.StreamChatAsync(summaryMessages, tools: null, options, ct))
        {
            if (chunk.TextDelta is not null)
                sb.Append(chunk.TextDelta);
        }

        return sb.ToString();
    }

    private static (List<Message> Messages, int Count, int Bytes) EvictLargeToolOutputs(List<Message> messages)
    {
        var evictedCount = 0;
        var evictedBytes = 0;
        var result = new List<Message>(messages.Count);

        foreach (var msg in messages)
        {
            if (msg.Role == MessageRole.Tool && (msg.Content?.Length ?? 0) > LargeToolOutputThreshold)
            {
                var originalLen = msg.Content!.Length;
                evictedBytes += originalLen;
                evictedCount++;
                result.Add(msg with
                {
                    Content = $"[Tool result evicted — was {originalLen} chars from {msg.ToolName ?? "unknown"}]",
                });
            }
            else
            {
                result.Add(msg);
            }
        }

        return (result, evictedCount, evictedBytes);
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

    private static int EstimateTokens(IReadOnlyList<Message> messages)
    {
        var totalChars = messages.Sum(m => (m.Content?.Length ?? 0) + 20);
        return totalChars / 4;
    }

    private static List<Message> GetRecentTurns(List<Message> messages, int keepTurns)
    {
        var nonSystem = messages.Where(m => m.Role != MessageRole.System).ToList();
        var turns = 0;
        var result = new List<Message>();

        for (var i = nonSystem.Count - 1; i >= 0 && turns < keepTurns; i--)
        {
            result.Insert(0, nonSystem[i]);
            if (nonSystem[i].Role == MessageRole.User)
                turns++;
        }

        return result;
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
