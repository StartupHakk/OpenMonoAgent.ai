using System.Text.Json;
using OpenMono.Session;

namespace OpenMono.Llm;

public sealed record LlmOptions
{
    public string Model { get; init; } = "";
    public double Temperature { get; init; } = 0.2;
    public int MaxTokens { get; init; } = 4096;
    public double TopP { get; init; } = 0.8;
    public int TopK { get; init; } = 20;
    public double PresencePenalty { get; init; } = 1.5;
    public double MinP { get; init; } = 0.0;
    public double RepetitionPenalty { get; init; } = 1.0;
    public bool? EnableThinking { get; init; }
}

public sealed record StreamChunk
{
    public string? ThinkingDelta { get; init; }
    public string? TextDelta { get; init; }
    public ToolCall? ToolCallDelta { get; init; }
    public bool IsComplete { get; init; }
    public UsageInfo? Usage { get; init; }
}

public sealed record UsageInfo
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens => PromptTokens + CompletionTokens;
}

public interface ILlmClient : IDisposable
{
    IAsyncEnumerable<StreamChunk> StreamChatAsync(
        IReadOnlyList<Message> messages,
        JsonElement? tools,
        LlmOptions options,
        CancellationToken ct);
}
