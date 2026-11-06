using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenMono.Session;

namespace OpenMono.Llm;

/// <summary>Tool calling through plain text for Foundry Local variants that reject the
/// structured `tools` field ("structured tool calling requires model-published opening and
/// closing tool markers"). The tool list goes into the system prompt in the Qwen XML form,
/// history is replayed without `role: tool` / `tool_calls`, and calls are parsed from content.</summary>
internal static class FoundryTextTools
{
    // Marker prefixes that start a text tool call in streamed content.
    public static readonly string[] Markers = ["<function=", "<tool_call>", "<tools>"];

    private static readonly Regex FunctionRegex = new(
        @"<function=([\w.\-]+)>(.*?)</function>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ParamRegex = new(
        @"<parameter=([\w.\-]+)>\s*(.*?)\s*</parameter>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ToolCallBlockRegex = new(
        @"<tool_call>(.*?)</tool_call>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ToolsBlockRegex = new(
        @"<tools>(.*?)</tools>",
        RegexOptions.Singleline | RegexOptions.Compiled);

    public static bool HasTools(JsonElement? tools) =>
        tools.HasValue && tools.Value.ValueKind == JsonValueKind.Array && tools.Value.GetArrayLength() > 0;

    public static string BuildPrompt(JsonElement tools)
    {
        var sb = new StringBuilder();
        sb.Append("# Tools\n\nYou have access to the following functions:\n\n<tools>");
        foreach (var tool in tools.EnumerateArray())
        {
            var fn = Function(tool);
            sb.Append('\n').Append(JsonSerializer.Serialize(new
            {
                type = "function",
                function = new
                {
                    name = Str(fn, "name"),
                    description = Str(fn, "description"),
                    parameters = fn.TryGetProperty("parameters", out var p) ? p : default(JsonElement?),
                },
            }));
        }

        sb.Append("\n</tools>\n\n");
        sb.Append("""
            If you choose to call a function ONLY reply in the following format with NO suffix:

            <tool_call>
            <function=example_function_name>
            <parameter=example_parameter_1>
            value_1
            </parameter>
            <parameter=example_parameter_2>
            This is the value for the second parameter
            that can span
            multiple lines
            </parameter>
            </function>
            </tool_call>

            <IMPORTANT>
            Reminder:
            - Function calls MUST follow the specified format: an inner <function=...></function> block must be nested within <tool_call></tool_call> XML tags
            - Required parameters MUST be specified
            - You may provide optional reasoning for your function call in natural language BEFORE the function call, but NOT after
            - If there is no function call available, answer the question like normal with your current knowledge and do not tell the user about function calls
            - Function results come back in a user message inside <tool_response></tool_response> tags
            </IMPORTANT>
            """);
        return sb.ToString();
    }

    /// <summary>Replays history without the OpenAI tool fields: assistant calls become their
    /// XML text and tool results become one user message of &lt;tool_response&gt; blocks.</summary>
    public static List<object> RewriteMessages(IReadOnlyList<Message> messages, JsonElement tools, Func<Message, object> convert)
    {
        var prompt = BuildPrompt(tools);
        var result = new List<object>();
        var promptPlaced = false;
        StringBuilder? pendingResults = null;

        void FlushResults()
        {
            if (pendingResults is null) return;
            result.Add(new { role = "user", content = pendingResults.ToString().TrimEnd() });
            pendingResults = null;
        }

        foreach (var m in messages)
        {
            if (m.Role == MessageRole.Tool)
            {
                pendingResults ??= new StringBuilder();
                pendingResults.Append("<tool_response>\n").Append(m.Content ?? "").Append("\n</tool_response>\n");
                continue;
            }

            FlushResults();
            switch (m.Role)
            {
                case MessageRole.System when !promptPlaced:
                    result.Add(new { role = "system", content = $"{m.Content}\n\n{prompt}".TrimStart() });
                    promptPlaced = true;
                    break;
                case MessageRole.Assistant when m.ToolCalls is { Count: > 0 }:
                    var text = new StringBuilder(m.Content ?? "");
                    foreach (var call in m.ToolCalls)
                        text.Append(text.Length > 0 ? "\n\n" : "").Append(FormatCall(call));
                    result.Add(new { role = "assistant", content = text.ToString() });
                    break;
                default:
                    result.Add(convert(m));
                    break;
            }
        }

        FlushResults();
        if (!promptPlaced)
            result.Insert(0, new { role = "system", content = prompt });
        return result;
    }

    public static string FormatCall(ToolCall call)
    {
        var sb = new StringBuilder("<tool_call>\n<function=").Append(call.Name).Append(">\n");
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.Arguments) ? "{}" : call.Arguments);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    var value = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
                    sb.Append("<parameter=").Append(p.Name).Append(">\n").Append(value).Append("\n</parameter>\n");
                }
            }
        }
        catch (JsonException)
        {
        }

        return sb.Append("</function>\n</tool_call>").ToString();
    }

    /// <summary>Index of the first text tool call marker, or -1.</summary>
    public static int MarkerIndex(string text, bool foundry)
    {
        var best = -1;
        foreach (var marker in foundry ? Markers : Markers[..1])
        {
            var i = text.IndexOf(marker, StringComparison.Ordinal);
            if (i >= 0 && (best < 0 || i < best)) best = i;
        }

        return best;
    }

    /// <summary>Length of a trailing fragment that could still grow into a marker, so
    /// streamed text does not show "&lt;tool_" before the call is recognised.</summary>
    public static int PartialMarkerLength(string text, bool foundry)
    {
        var lt = text.LastIndexOf('<');
        if (lt < 0) return 0;
        var tail = text[lt..];
        foreach (var marker in foundry ? Markers : Markers[..1])
        {
            if (tail.Length < marker.Length && marker.StartsWith(tail, StringComparison.Ordinal))
                return tail.Length;
        }

        return 0;
    }

    /// <summary>Tool calls written as text: Qwen XML (&lt;function=..&gt;, bare or inside
    /// &lt;tool_call&gt;), Hermes JSON inside &lt;tool_call&gt;, and JSON inside &lt;tools&gt;.</summary>
    public static List<(string Name, string Arguments)> Parse(string text, JsonElement? tools, bool foundry)
    {
        var calls = new List<(string, string)>();
        foreach (Match m in FunctionRegex.Matches(text))
        {
            var name = m.Groups[1].Value;
            var args = new Dictionary<string, object?>();
            foreach (Match p in ParamRegex.Matches(m.Groups[2].Value))
                args[p.Groups[1].Value] = Coerce(p.Groups[2].Value.Trim(), foundry ? ParamType(tools, name, p.Groups[1].Value) : null);
            calls.Add((name, JsonSerializer.Serialize(args)));
        }

        if (calls.Count > 0 || !foundry)
            return calls;

        foreach (var regex in new[] { ToolCallBlockRegex, ToolsBlockRegex })
        {
            foreach (Match m in regex.Matches(text))
                calls.AddRange(ParseJsonCalls(m.Groups[1].Value));
            if (calls.Count > 0) break;
        }

        return calls;
    }

    private static IEnumerable<(string, string)> ParseJsonCalls(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json.Trim());
        }
        catch (JsonException)
        {
            yield break;
        }

        using (doc)
        {
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : [doc.RootElement];
            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var fn = Function(item);
                if (Str(fn, "name") is not { Length: > 0 } name) continue;
                var args = fn.TryGetProperty("arguments", out var a) || fn.TryGetProperty("parameters", out a)
                    ? a.ValueKind == JsonValueKind.String ? a.GetString() ?? "{}" : a.GetRawText()
                    : "{}";
                yield return (name, args);
            }
        }
    }

    // A parameter value is text; a schema type other than string makes it JSON when it parses.
    private static object? Coerce(string value, string? type)
    {
        if (type is null or "string") return value;
        try
        {
            using var doc = JsonDocument.Parse(value);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return value;
        }
    }

    private static string? ParamType(JsonElement? tools, string function, string parameter)
    {
        if (!HasTools(tools)) return null;
        foreach (var tool in tools!.Value.EnumerateArray())
        {
            var fn = Function(tool);
            if (Str(fn, "name") != function) continue;
            if (fn.TryGetProperty("parameters", out var ps) && ps.ValueKind == JsonValueKind.Object
                && ps.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
                && props.TryGetProperty(parameter, out var schema) && schema.ValueKind == JsonValueKind.Object)
                return Str(schema, "type");
        }

        return null;
    }

    private static JsonElement Function(JsonElement tool) =>
        tool.ValueKind == JsonValueKind.Object && tool.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object
            ? fn : tool;

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
