using System.Text;
using OpenMono.Llm;
using OpenMono.Session;

namespace OpenMono.Commands;

public sealed class BtwCommand : ICommand
{
    private const int MaxContextMessages = 6;

    private const string SystemPrompt =
        "You are OpenMono, answering a quick '/btw' aside — a tangential question the user has outside the main task flow. " +
        "Answer directly and concisely in plain text. You have no tools available for this reply.";

    public string Name => "btw";
    public string Description => "Ask a quick aside — answered directly by the model, skipping tool use and context bookkeeping";
    public CommandType Type => CommandType.Local;

    // Safe to dispatch while a main turn is actively streaming/running tools: it never
    // touches session.Messages and never calls the streaming-bubble renderer APIs
    // (StartAssistantResponse/StreamText/EndAssistantResponse), which share single-slot
    // buffers with whatever the active turn is currently rendering. Printing the answer
    // via WriteMarkdown instead goes through the renderer's message-list path, which is
    // already safe to call concurrently with an active turn (that's how tool-progress
    // output interleaves with streaming today).
    public bool SafeDuringActiveTurn => true;

    public async Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var question = args.Length > 0 ? string.Join(' ', args).Trim() : "";
        if (string.IsNullOrEmpty(question))
        {
            context.Renderer.WriteWarning("Usage: /btw <question> — ask a quick aside without touching tools or the main conversation context.");
            return;
        }

        var recentContext = BuildRecentContext(context.Session.Messages);
        var messages = new List<Message>
        {
            new() { Role = MessageRole.System, Content = SystemPrompt },
            new()
            {
                Role = MessageRole.User,
                Content = recentContext.Length > 0
                    ? $"Recent conversation, for context only:\n{recentContext}\n---\nQuick aside: {question}"
                    : question,
            },
        };

        var options = new LlmOptions
        {
            Model = context.Config.Llm.Model,
            MaxTokens = 1024,
            Temperature = 0.3,
        };

        try
        {
            var sb = new StringBuilder();
            await foreach (var chunk in context.Llm.StreamChatAsync(messages, tools: null, options, ct))
            {
                if (chunk.TextDelta is not null)
                    sb.Append(chunk.TextDelta);
            }

            context.Renderer.WriteMarkdown(sb.Length > 0 ? sb.ToString() : "(no response)");
        }
        catch (OperationCanceledException)
        {
            context.Renderer.WriteWarning("Aside cancelled.");
        }
        catch (HttpRequestException ex)
        {
            context.Renderer.WriteError($"LLM error: {ex.Message}");
        }
        catch (Exception ex)
        {
            context.Renderer.WriteError($"Unexpected error: {ex.Message}");
        }
    }

    private static string BuildRecentContext(List<Message> messages)
    {
        var recent = messages
            .Where(m => m.Role is MessageRole.User or MessageRole.Assistant && !string.IsNullOrWhiteSpace(m.Content))
            .TakeLast(MaxContextMessages)
            .ToList();

        if (recent.Count == 0) return "";

        var sb = new StringBuilder();
        foreach (var msg in recent)
            sb.AppendLine($"[{msg.Role.ToString().ToUpperInvariant()}]: {msg.Content}");
        return sb.ToString();
    }
}
