using OpenMono.Llm;
using OpenMono.Session;

namespace OpenMono.Utils;

public static class SummarySafety
{
    public const int LargeToolOutputThreshold = 2000;

    public static (List<Message> Messages, int Count, int Bytes) EvictLargeToolOutputs(List<Message> messages, int threshold = LargeToolOutputThreshold)
    {
        var evictedCount = 0;
        var evictedBytes = 0;
        var result = new List<Message>(messages.Count);

        foreach (var msg in messages)
        {
            if (msg.Role == MessageRole.Tool && (msg.Content?.Length ?? 0) > threshold)
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

    public static void EnsureSummaryFits(IReadOnlyList<Message> summaryMessages, int contextSize)
    {
        var tokens = TokenEstimate.EstimatePayload(summaryMessages);
        var threshold = (int)(contextSize * 0.80);
        if (tokens > threshold)
        {
            throw new ContextOverflowException(
                $"Summary prompt ({tokens} est. tokens) exceeds the context window " +
                $"({contextSize}); cannot summarize. {tokens - threshold} tokens over threshold.");
        }
    }
}
