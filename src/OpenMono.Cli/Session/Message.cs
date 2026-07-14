using System.Text.Json.Serialization;

namespace OpenMono.Session;

[JsonConverter(typeof(JsonStringEnumConverter<MessageRole>))]
public enum MessageRole
{
    System,
    User,
    Assistant,
    Tool
}

public sealed record Message
{
    public required MessageRole Role { get; init; }
    public string? Content { get; init; }
    public IReadOnlyList<ContentPart>? ContentParts { get; init; }
    public List<ToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    /// <summary>
    /// For Tool-role messages: whether the tool result represents a failure
    /// (denied, blocked, invalid, or crashed). Surfaced to the model as the
    /// provider's structured error signal (e.g. Anthropic's tool_result.is_error)
    /// so a failure cannot be misread as success.
    /// </summary>
    public bool IsError { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed record ToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Arguments { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextPart), "text")]
[JsonDerivedType(typeof(ImagePart), "image")]
public abstract record ContentPart;
public sealed record TextPart(string Text) : ContentPart;
public sealed record ImagePart(string Url) : ContentPart;
