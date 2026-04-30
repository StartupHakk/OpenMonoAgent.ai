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
    public List<ToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
    public string? ToolName { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

public sealed record ToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Arguments { get; init; }
}
