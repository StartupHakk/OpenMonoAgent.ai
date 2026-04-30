using OpenMono.Llm;

namespace OpenMono.Session;

public sealed class Compactor
{
    private readonly ILlmClient _llm;
    private readonly int _contextSize;

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

    public async Task<SessionState> CompactAsync(SessionState session, CancellationToken ct)
    {
        var messages = session.Messages;
        if (messages.Count < 6) return session;

        var systemMessages = messages.Where(m => m.Role == MessageRole.System).ToList();
        var recentTurns = GetRecentTurns(messages, keepTurns: 4);

        var toSummarize = messages
            .Except(systemMessages)
            .Except(recentTurns)
            .ToList();

        if (toSummarize.Count < 4) return session;

        var summaryPrompt = BuildSummaryPrompt(toSummarize);
        var summaryMessages = new List<Message>
        {
            new() { Role = MessageRole.System, Content = "You are a conversation summarizer. Produce a concise summary of the conversation history below. Focus on: decisions made, files modified, errors encountered, and current task state. Be factual and brief." },
            new() { Role = MessageRole.User, Content = summaryPrompt }
        };

        var summary = "";
        var options = new LlmOptions { MaxTokens = 1024, Temperature = 0.1 };

        await foreach (var chunk in _llm.StreamChatAsync(summaryMessages, tools: null, options, ct))
        {
            if (chunk.TextDelta is not null)
                summary += chunk.TextDelta;
        }

        var compacted = new SessionState();
        foreach (var msg in systemMessages)
            compacted.AddMessage(msg);

        compacted.AddMessage(new Message
        {
            Role = MessageRole.User,
            Content = $"[Conversation summary - {toSummarize.Count} messages compacted]\n\n{summary}",
        });

        compacted.AddMessage(new Message
        {
            Role = MessageRole.Assistant,
            Content = "Understood. I have the context from the summarized conversation. Let's continue.",
        });

        foreach (var msg in recentTurns)
            compacted.AddMessage(msg);

        compacted.TotalTokensUsed = session.TotalTokensUsed;
        compacted.TurnCount = session.TurnCount;

        return compacted;
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

    private static string BuildSummaryPrompt(List<Message> messages)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Summarize this conversation history:\n");

        foreach (var msg in messages)
        {
            var role = msg.Role.ToString().ToUpperInvariant();
            var content = msg.Content ?? "(tool call)";
            if (content.Length > 500)
                content = content[..500] + "...";
            sb.AppendLine($"[{role}]: {content}\n");
        }

        return sb.ToString();
    }
}
