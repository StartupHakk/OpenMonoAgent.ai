using System.Text.Json;
using OpenMono.Utils;

namespace OpenMono.Session;

/// <summary>
/// Repairs session history that may contain broken tool-call JSON (e.g. from an older version
/// that stored arguments truncated mid-JSON at max_tokens). Providers reject such requests with
/// a 400, so every request body must be built from sanitized messages. The in-memory session is
/// never mutated — sanitizing is done per-request on the copy being sent.
/// </summary>
public static class MessageSanitizer
{
    public const string BrokenArgumentsMarker = "{\"error\":\"truncated\"}";

    /// <summary>Returns a request-safe copy of <paramref name="messages"/> where every assistant
    /// tool-call argument is valid JSON and every tool message references an existing call.</summary>
    public static List<Message> SanitizeForRequest(IReadOnlyList<Message> messages)
    {
        var result = new List<Message>(messages.Count);
        var knownCallIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var msg in messages)
        {
            if (msg.Role == MessageRole.Assistant && msg.ToolCalls is { Count: > 0 } calls)
            {
                var repaired = false;
                var fixedCalls = new List<ToolCall>(calls.Count);
                foreach (var tc in calls)
                {
                    knownCallIds.Add(tc.Id);
                    if (IsValidJsonObject(tc.Arguments))
                    {
                        fixedCalls.Add(tc);
                    }
                    else
                    {
                        repaired = true;
                        Log.Warn($"[SANITIZE] Replacing broken tool-call arguments for {tc.Name} ({tc.Id})");
                        fixedCalls.Add(tc with { Arguments = BrokenArgumentsMarker });
                    }
                }

                if (repaired)
                    result.Add(msg with { ToolCalls = fixedCalls });
                else
                    result.Add(msg);
            }
            else if (msg.Role == MessageRole.Tool && msg.ToolCallId is not null)
            {
                // A tool message whose call was dropped/unknown would 400 on OpenAI-compatible
                // providers. Demote it to a plain user note instead.
                if (!knownCallIds.Contains(msg.ToolCallId))
                {
                    Log.Warn($"[SANITIZE] Demoting orphaned tool message (call {msg.ToolCallId})");
                    result.Add(new Message
                    {
                        Role = MessageRole.User,
                        Content = $"[Tool result from earlier call {msg.ToolName ?? "unknown"}]\n{msg.Content}",
                        Timestamp = msg.Timestamp,
                    });
                }
                else
                {
                    result.Add(msg);
                }
            }
            else
            {
                result.Add(msg);
            }
        }

        return result;
    }

    public static bool IsValidJsonObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
