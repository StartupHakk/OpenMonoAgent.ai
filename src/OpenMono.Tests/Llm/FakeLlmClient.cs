using OpenMono.Llm;
using OpenMono.Session;

namespace OpenMono.Tests.Llm;

/// <summary>
/// Minimal <see cref="ILlmClient"/> for tests that only care about *which* client a
/// resolver hands back, not about wire protocol. Records each call and emits one text chunk.
/// </summary>
internal sealed class FakeLlmClient : ILlmClient
{
    public string Label { get; }
    public int CallCount { get; private set; }
    public string? LastModel { get; private set; }
    public bool Disposed { get; private set; }

    public FakeLlmClient(string label = "fake")
    {
        Label = label;
    }

    public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
        IReadOnlyList<Message> messages,
        System.Text.Json.JsonElement? tools,
        LlmOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        CallCount++;
        LastModel = options.Model;
        yield return new StreamChunk
        {
            TextDelta = Label,
            IsComplete = true,
        };
        await Task.CompletedTask;
    }

    public void Dispose() => Disposed = true;
}
