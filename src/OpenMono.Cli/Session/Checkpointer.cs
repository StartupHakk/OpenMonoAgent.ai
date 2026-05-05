using System.Text;
using OpenMono.Llm;

namespace OpenMono.Session;

public sealed class Checkpointer
{
    private readonly ILlmClient _llm;
    private readonly int _contextSize;

    private const double TriggerThreshold = 0.65;
    private const int KeepRecentTurns = 4;

    public Checkpointer(ILlmClient llm, int contextSize)
    {
        _llm = llm;
        _contextSize = contextSize;
    }

    public bool NeedsCheckpoint(SessionState session, int lastPromptTokens = 0)
    {
        if (!HasCompressibleContent(session))
            return false;

        int tokens;
        if (lastPromptTokens > 0)
        {
            tokens = lastPromptTokens;
        }
        else
        {
            var effective = BuildContextWindow(session);
            tokens = EstimateTokens(effective);
        }
        var threshold = (int)(_contextSize * TriggerThreshold);
        return tokens > threshold;
    }

    public bool HasCompressibleContent(SessionState session)
    {
        var prevCutoff = session.CheckpointCutoffIndex;
        for (var keep = KeepRecentTurns; keep >= 1; keep--)
        {
            var candidate = FindRecentStartIndex(session.Messages, keep);
            if (candidate <= prevCutoff) continue;

            var hasContent = session.Messages
                .Skip(prevCutoff)
                .Take(candidate - prevCutoff)
                .Any(m => m.Role != MessageRole.System);

            if (hasContent) return true;
        }
        return false;
    }

    public async Task<CheckpointEntry> CreateCheckpointAsync(SessionState session, CancellationToken ct)
    {
        var prevCutoff = session.CheckpointCutoffIndex;

        var cutoff = 0;
        for (var keep = KeepRecentTurns; keep >= 1; keep--)
        {
            var candidate = FindRecentStartIndex(session.Messages, keep);
            if (candidate <= prevCutoff) continue;

            var hasContent = session.Messages
                .Skip(prevCutoff)
                .Take(candidate - prevCutoff)
                .Any(m => m.Role != MessageRole.System);

            if (hasContent)
            {
                cutoff = candidate;
                break;
            }
        }

        if (cutoff <= prevCutoff)
            throw new InvalidOperationException(
                "Nothing to compress: all messages are within the recent-turns window.");

        var toSummarise = session.Messages
            .Skip(prevCutoff)
            .Take(cutoff - prevCutoff)
            .Where(m => m.Role != MessageRole.System)
            .ToList();

        var summary = await GenerateSummaryAsync(toSummarise, ct);

        var entry = new CheckpointEntry
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            CreatedAt = DateTime.UtcNow,
            TurnIndex = session.TurnCount,
            CutoffMessageIndex = cutoff,
            Summary = summary,
            MessagesCompressed = toSummarise.Count,
        };

        session.Checkpoints.Add(entry);
        session.CheckpointCutoffIndex = cutoff;
        return entry;
    }

    public List<Message> BuildContextWindow(SessionState session)
    {
        var latest = session.Checkpoints.LastOrDefault();
        if (latest is null)
            return session.Messages;

        var system = session.Messages.Where(m => m.Role == MessageRole.System).ToList();
        var recent = session.Messages.Skip(latest.CutoffMessageIndex).ToList();

        var window = new List<Message>(system.Count + 2 + recent.Count);
        window.AddRange(system);
        window.Add(new Message
        {
            Role = MessageRole.User,
            Content = $"[Checkpoint #{session.Checkpoints.Count} — {latest.CreatedAt:yyyy-MM-dd HH:mm} UTC, turn {latest.TurnIndex}]\n\n{latest.Summary}",
        });
        window.Add(new Message
        {
            Role = MessageRole.Assistant,
            Content = "Understood. I have the full context from the checkpoint. Continuing from where we left off.",
        });
        window.AddRange(recent);
        return window;
    }

    private async Task<string> GenerateSummaryAsync(List<Message> messages, CancellationToken ct)
    {
        var conversationText = BuildConversationText(messages);

        var summaryMessages = new List<Message>
        {
            new() { Role = MessageRole.System, Content = SummaryPrompt.BuildPrompt() },
            new() { Role = MessageRole.User,   Content = conversationText },
        };

        var sb = new StringBuilder();
        var opts = new LlmOptions { MaxTokens = 4096, Temperature = 0.1 };

        await foreach (var chunk in _llm.StreamChatAsync(summaryMessages, tools: null, opts, ct))
        {
            if (chunk.TextDelta is not null)
                sb.Append(chunk.TextDelta);
        }

        return SummaryPrompt.FormatSummary(sb.ToString());
    }

    private static int FindRecentStartIndex(List<Message> messages, int keepTurns)
    {
        var userTurnsSeen = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == MessageRole.User)
            {
                userTurnsSeen++;
                if (userTurnsSeen >= keepTurns)
                    return i;
            }
        }
        return 0;
    }

    internal static int EstimateTokens(IReadOnlyList<Message> messages)
    {
        var chars = messages.Sum(m => (m.Content?.Length ?? 0) + 20);
        return chars / 4;
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
}
