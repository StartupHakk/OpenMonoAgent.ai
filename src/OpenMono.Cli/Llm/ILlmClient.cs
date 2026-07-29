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

    /// <summary>When set, constrains decoding to this JSON Schema (grammar-based on llama.cpp) so the
    /// response is guaranteed-valid JSON — no parsing/retry needed on providers that support it.</summary>
    public JsonElement? ResponseFormatSchema { get; init; }
}

public sealed record StreamChunk
{
    public string? ThinkingDelta { get; init; }
    public string? TextDelta { get; init; }
    public ToolCall? ToolCallDelta { get; init; }
    public string? ToolCallProgress { get; init; }
    public bool IsComplete { get; init; }
    public UsageInfo? Usage { get; init; }
}

public sealed record UsageInfo
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens => PromptTokens + CompletionTokens;

    // Generation throughput from llama.cpp's `timings` block (when present on the response).
    // PredictedTokens/PredictedMs are accumulated for a rolling average; PredictedPerSecond is
    // the server's own live decode rate for this turn. All default to 0 on providers that omit timings.
    public int PredictedTokens { get; init; }
    public double PredictedMs { get; init; }
    public double PredictedPerSecond { get; init; }
}

public interface ILlmClient : IDisposable
{
    IAsyncEnumerable<StreamChunk> StreamChatAsync(
        IReadOnlyList<Message> messages,
        JsonElement? tools,
        LlmOptions options,
        CancellationToken ct);
}
