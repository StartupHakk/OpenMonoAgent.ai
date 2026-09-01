using System.Text.Json;
using OpenMono.Config;
using OpenMono.Llm;
using OpenMono.Session;

namespace OpenMono.Utils;

public static class TokenEstimate
{
    public static int Estimate(string json) => json.Length / 4;

    public static int EstimatePayload(IReadOnlyList<Message> messages, JsonElement? tools = null, string model = "default")
        => Estimate(SerializePayload(messages, tools, model));

    public static string SerializePayload(IReadOnlyList<Message> messages, JsonElement? tools = null, string model = "default")
    {
        var messagesJson = SerializeMessages(messages);
        var toolsJson = tools is { ValueKind: JsonValueKind.Array, } t
            ? JsonSerializer.Serialize(t, JsonOptions.Default)
            : "[]";
        return $"{{\"model\":\"{model}\",\"messages\":{messagesJson},\"tools\":{toolsJson},\"stream\":true,\"max_tokens\":4096}}";
    }

    private static string SerializeMessages(IReadOnlyList<Message> messages)
    {
        var sb = new System.Text.StringBuilder("[");
        for (var i = 0; i < messages.Count; i++)
        {
            sb.Append(SerializeMessage(messages[i]));
            if (i < messages.Count - 1) sb.Append(',');
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string SerializeMessage(Message m)
    {
        return m.Role switch
        {
            MessageRole.System => $"{{\"role\":\"system\",\"content\":{JsonStr(m.Content)}}}",
            MessageRole.User when m.ContentParts is { Count: > 0 } =>
                $"{{\"role\":\"user\",\"content\":{SerializeContentParts(m.ContentParts)}}}",
            MessageRole.User => $"{{\"role\":\"user\",\"content\":{JsonStr(m.Content)}}}",
            MessageRole.Assistant when m.ToolCalls is { Count: > 0 } =>
                $"{{\"role\":\"assistant\",\"content\":{JsonStr(m.Content ?? "")},\"tool_calls\":{SerializeToolCalls(m.ToolCalls)}}}",
            MessageRole.Assistant => $"{{\"role\":\"assistant\",\"content\":{JsonStr(m.Content)}}}",
            MessageRole.Tool => $"{{\"role\":\"tool\",\"tool_call_id\":{JsonStr(m.ToolCallId)},\"content\":{JsonStr(m.Content)}}}",
            _ => $"{{\"role\":\"user\",\"content\":{JsonStr(m.Content)}}}",
        };
    }

    private static string SerializeContentParts(IReadOnlyList<ContentPart> parts)
    {
        var sb = new System.Text.StringBuilder("[");
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            // Media is stripped: an image contributes a fixed placeholder, not its (possibly
            // megabyte-scale) base64 URL, mirroring OpenCode's stripMedia.
            sb.Append(part switch
            {
                TextPart t => $"{{\"type\":\"text\",\"text\":{JsonStr(t.Text)}}}",
                ImagePart => "{\"type\":\"image_url\",\"image_url\":{\"url\":\"[image stripped]\"}}",
                _ => "{\"type\":\"text\",\"text\":\"\"}",
            });
            if (i < parts.Count - 1) sb.Append(',');
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string SerializeToolCalls(IReadOnlyList<ToolCall> calls)
    {
        var sb = new System.Text.StringBuilder("[");
        for (var i = 0; i < calls.Count; i++)
        {
            var c = calls[i];
            sb.Append($"{{\"id\":{JsonStr(c.Id)},\"type\":\"function\",\"function\":{{\"name\":{JsonStr(c.Name)},\"arguments\":{JsonStr(c.Arguments)}}}}}");
            if (i < calls.Count - 1) sb.Append(',');
        }
        sb.Append(']');
        return sb.ToString();
    }

    private static string JsonStr(string? value)
        => value is null ? "null" : JsonSerializer.Serialize(value, JsonOptions.Default);
}
