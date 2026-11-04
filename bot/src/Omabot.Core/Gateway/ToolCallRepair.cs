// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Omabot.Gateway;

/// <summary>One tool call in an assembled reply. Arguments stay text until repaired.</summary>
public sealed class AssembledCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "";
}

/// <summary>A whole model reply, from a JSON body or an SSE stream, ready to repair and send on.</summary>
public sealed class AssembledReply
{
    public string Id { get; set; } = "chatcmpl-roster";
    public string Model { get; set; } = "";
    public long Created { get; set; }
    public string? Content { get; set; }
    public string? Reasoning { get; set; }
    public List<AssembledCall> Calls { get; } = [];
    public string? FinishReason { get; set; }
    public JsonNode? Usage { get; set; }
}

public sealed record RepairStats(int Native, int Repaired, int Unreadable, int Recovered, int Coerced, int Dropped)
{
    public bool Changed => Repaired + Recovered + Coerced + Dropped + Unreadable > 0;
}

/// <summary>
/// The gateway's tool-call repair layer (plan 7.8, adapted from the idea of
/// chat-text-tool-calls, re-implemented). It runs only when the request
/// offered tools. Broken native arguments are repaired by trimming text after
/// the last brace, closing what is open at the very end, or removing a
/// trailing comma, and never by guessing values; calls written as text are
/// recovered only when they end the reply and name an offered tool, and
/// never from reasoning; string values become numbers or booleans when the
/// schema says so; at most 32 calls are kept. A recovered call reaches OMA as
/// a normal tool call, so OMA's schema and permission checks still apply.
/// </summary>
public static partial class ToolCallRepair
{
    public const int MaxCalls = 32;

    /// <summary>Offered tools by name, with their parameter schemas.</summary>
    public static Dictionary<string, JsonObject?> OfferedTools(JsonObject request)
    {
        var map = new Dictionary<string, JsonObject?>(StringComparer.Ordinal);
        if (request["tools"] is not JsonArray tools) return map;
        foreach (var t in tools.OfType<JsonObject>())
        {
            var fn = t["function"] as JsonObject ?? t;
            if (fn["name"] is JsonValue n && n.TryGetValue<string>(out var name) && name.Length > 0) map[name] = fn["parameters"] as JsonObject;
        }
        return map;
    }

    // ------------------------------------------------------------ rule 2

    private static bool IsObject(string s, out JsonObject? obj)
    {
        obj = null;
        try
        {
            obj = JsonNode.Parse(s) as JsonObject;
            return obj is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A valid JSON object for broken arguments, or null when no allowed repair works.</summary>
    public static string? RepairArguments(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return "{}";
        var tried = new List<string> { s };
        var trimmed = TrimAfterFirstObject(s);
        tried.Add(trimmed);
        tried.Add(TrimAfterLastBrace(s));
        tried.Add(RemoveTrailingCommas(s));
        tried.Add(RemoveTrailingCommas(trimmed));
        if (CloseAtEnd(s) is { } closed)
        {
            tried.Add(closed);
            tried.Add(RemoveTrailingCommas(closed));
        }
        if (CloseAtEnd(RemoveTrailingCommas(s)) is { } closed2) tried.Add(closed2);
        foreach (var c in tried.Distinct(StringComparer.Ordinal))
            if (IsObject(c, out var obj)) return obj!.ToJsonString();
        return null;
    }

    /// <summary>Cuts everything after the brace that closes the first object (strings respected).</summary>
    public static string TrimAfterFirstObject(string s)
    {
        var start = s.IndexOf('{');
        if (start < 0) return s;
        var depth = 0;
        var inString = false;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return s[..(i + 1)];
        }
        return s;
    }

    public static string TrimAfterLastBrace(string s)
    {
        var i = s.LastIndexOf('}');
        return i < 0 ? s : s[..(i + 1)];
    }

    /// <summary>Removes a comma that comes right before a closing brace or bracket, outside strings.</summary>
    public static string RemoveTrailingCommas(string s)
    {
        var sb = new StringBuilder(s.Length);
        var inString = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                sb.Append(c);
                if (c == '\\' && i + 1 < s.Length) sb.Append(s[++i]);
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            if (c == ',')
            {
                var j = i + 1;
                while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
                if (j < s.Length && s[j] is '}' or ']') continue;
                if (j == s.Length) continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Closes an unclosed string and then open braces and brackets, only by
    /// appending at the very end. Null when the cut leaves a key or a colon
    /// with no value, because closing then would mean guessing.
    /// </summary>
    public static string? CloseAtEnd(string s)
    {
        var stack = new Stack<char>();
        var inString = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': stack.Push('}'); break;
                case '[': stack.Push(']'); break;
                case '}' or ']':
                    if (stack.Count == 0 || stack.Pop() != c) return null;
                    break;
            }
        }
        if (stack.Count == 0 && !inString) return null;
        var sb = new StringBuilder(s);
        if (inString)
        {
            if (s.EndsWith('\\')) sb.Length--;
            sb.Append('"');
        }
        var tail = sb.ToString().TrimEnd();
        if (tail.EndsWith(':') || tail.EndsWith(',') || tail.EndsWith('{') && stack.Count == 0) return null;
        if (!inString && IsKeyWithoutValue(tail)) return null;
        while (stack.Count > 0) sb.Append(stack.Pop());
        return sb.ToString();
    }

    /// <summary>True when the text ends with a string that is a key (a "{" or "," before it, no colon after).</summary>
    private static bool IsKeyWithoutValue(string tail)
    {
        if (!tail.EndsWith('"')) return false;
        var i = tail.Length - 2;
        while (i >= 0 && !(tail[i] == '"' && (i == 0 || tail[i - 1] != '\\'))) i--;
        if (i <= 0) return false;
        var j = i - 1;
        while (j >= 0 && char.IsWhiteSpace(tail[j])) j--;
        return j >= 0 && tail[j] is '{' or ',';
    }

    // ------------------------------------------------------------ rule 3

    [GeneratedRegex(@"<tool_call>\s*(?<json>\{.*?\})\s*</tool_call>", RegexOptions.Singleline)]
    private static partial Regex HermesCall();

    [GeneratedRegex(@"(?:<tool_call>\s*)?<function=(?<name>[A-Za-z0-9_\-.]+)>(?<body>.*?)</function>(?:\s*</tool_call>)?", RegexOptions.Singleline)]
    private static partial Regex CoderCall();

    [GeneratedRegex(@"<parameter=(?<key>[A-Za-z0-9_\-.]+)>\n?(?<value>.*?)\n?</parameter>", RegexOptions.Singleline)]
    private static partial Regex CoderParameter();

    /// <summary>
    /// Calls written as text at the end of the reply, for offered tools only.
    /// Reasoning (up to the last &lt;/think&gt;) is never read. Returns the
    /// content without the calls, or the content unchanged and no calls.
    /// </summary>
    public static (string Content, List<(string Name, JsonObject Args)> Calls) RecoverTextCalls(string content, IReadOnlyDictionary<string, JsonObject?> offered)
    {
        var none = (content, new List<(string, JsonObject)>());
        if (offered.Count == 0) return none;
        var think = content.LastIndexOf("</think>", StringComparison.Ordinal);
        var start = think < 0 ? 0 : think + "</think>".Length;
        var visible = content[start..];

        foreach (var (regex, parse) in new (Regex, Func<Match, (string Name, JsonObject? Args)>)[] { (HermesCall(), ParseHermes), (CoderCall(), ParseCoder) })
        {
            var matches = regex.Matches(visible).ToList();
            if (matches.Count == 0) continue;
            // The calls must end the reply: walk back from the end over whitespace-separated calls.
            var tail = new List<Match>();
            var end = visible.TrimEnd().Length;
            for (var i = matches.Count - 1; i >= 0; i--)
            {
                var m = matches[i];
                if (visible[(m.Index + m.Length)..end].Trim().Length != 0) break;
                tail.Insert(0, m);
                end = m.Index;
            }
            if (tail.Count == 0) continue;
            var calls = new List<(string, JsonObject)>();
            foreach (var m in tail)
            {
                var (name, args) = parse(m);
                if (args is null || !offered.ContainsKey(name)) return none;
                calls.Add((name, args));
            }
            return (content[..(start + tail[0].Index)].TrimEnd(), calls);
        }

        // A reply that is only one JSON call.
        var trimmed = visible.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}') && IsObject(trimmed, out var only) && only!["name"] is JsonValue nv && nv.TryGetValue<string>(out var onlyName)
            && offered.ContainsKey(onlyName) && ArgsOf(only["arguments"] ?? only["parameters"]) is { } onlyArgs && only.Count <= 3)
            return (content[..start].TrimEnd(), [(onlyName, onlyArgs)]);
        return none;
    }

    private static (string, JsonObject?) ParseHermes(Match m)
    {
        var json = m.Groups["json"].Value;
        if (!IsObject(json, out var obj) && RepairArguments(json) is { } fixedJson) IsObject(fixedJson, out obj);
        if (obj?["name"] is not JsonValue n || !n.TryGetValue<string>(out var name)) return ("", null);
        return (name, ArgsOf(obj["arguments"] ?? obj["parameters"]));
    }

    private static (string, JsonObject?) ParseCoder(Match m)
    {
        var args = new JsonObject();
        foreach (Match p in CoderParameter().Matches(m.Groups["body"].Value))
        {
            var value = p.Groups["value"].Value;
            args[p.Groups["key"].Value] = value.TrimStart().StartsWith('{') || value.TrimStart().StartsWith('[')
                ? TryParseNode(value) ?? JsonValue.Create(value)
                : JsonValue.Create(value);
        }
        return (m.Groups["name"].Value, args);
    }

    private static JsonNode? TryParseNode(string s)
    {
        try { return JsonNode.Parse(s); }
        catch (JsonException) { return null; }
    }

    private static JsonObject? ArgsOf(JsonNode? node) => node switch
    {
        JsonObject o => (JsonObject)o.DeepClone(),
        JsonValue v when v.TryGetValue<string>(out var s) => RepairArguments(s) is { } r && IsObject(r, out var obj) ? obj : null,
        null => [],
        _ => null,
    };

    // ------------------------------------------------------------ rule 4

    /// <summary>Converts string values to the schema's number, integer or boolean type when they read cleanly. Returns how many changed.</summary>
    public static int Coerce(JsonObject args, JsonObject? schema)
    {
        if (schema?["properties"] is not JsonObject props) return 0;
        var n = 0;
        foreach (var (key, propSchema) in props)
        {
            if (propSchema is not JsonObject ps || !args.ContainsKey(key)) continue;
            var value = args[key];
            var types = TypesOf(ps);
            if (value is JsonValue v && v.TryGetValue<string>(out var s) && Convert(s, types) is { } converted)
            {
                args[key] = converted;
                n++;
            }
            else if (value is JsonObject child && types.Contains("object")) n += Coerce(child, ps);
            else if (value is JsonArray arr && ps["items"] is JsonObject items)
            {
                var itemTypes = TypesOf(items);
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue iv && iv.TryGetValue<string>(out var istr) && Convert(istr, itemTypes) is { } ic)
                    {
                        arr[i] = ic;
                        n++;
                    }
                    else if (arr[i] is JsonObject io && itemTypes.Contains("object")) n += Coerce(io, items);
                }
            }
        }
        return n;
    }

    private static HashSet<string> TypesOf(JsonObject schema) => schema["type"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var t) => [t],
        JsonArray a => [.. a.OfType<JsonValue>().Select(x => x.TryGetValue<string>(out var t) ? t : "")],
        _ => [],
    };

    private static JsonNode? Convert(string s, HashSet<string> types)
    {
        if (types.Contains("string")) return null;
        var t = s.Trim();
        if (types.Contains("integer") && long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
        if (types.Contains("number") && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)) return JsonValue.Create(d);
        if (types.Contains("boolean") && (t.Equals("true", StringComparison.OrdinalIgnoreCase) || t.Equals("false", StringComparison.OrdinalIgnoreCase)))
            return JsonValue.Create(t.Equals("true", StringComparison.OrdinalIgnoreCase));
        return null;
    }

    // ------------------------------------------------------------ the whole reply

    /// <summary>Applies every rule to an assembled reply. Unreadable calls are taken out and returned for the one resend.</summary>
    public static (RepairStats Stats, List<AssembledCall> Unreadable) Repair(AssembledReply reply, IReadOnlyDictionary<string, JsonObject?> offered)
    {
        int repaired = 0, coerced = 0, recovered = 0, dropped = 0;
        var native = reply.Calls.Count;
        var unreadable = new List<AssembledCall>();
        foreach (var call in reply.Calls.ToList())
        {
            if (!IsObject(call.Arguments.Trim().Length == 0 ? "{}" : call.Arguments, out var args))
            {
                var fixedArgs = RepairArguments(call.Arguments);
                if (fixedArgs is null)
                {
                    unreadable.Add(call);
                    reply.Calls.Remove(call);
                    continue;
                }
                IsObject(fixedArgs, out args);
                repaired++;
            }
            if (offered.TryGetValue(call.Name, out var schema)) coerced += Coerce(args!, schema);
            call.Arguments = args!.ToJsonString();
        }
        if (native == 0 && reply.Content is { Length: > 0 } content)
        {
            var (rest, calls) = RecoverTextCalls(content, offered);
            if (calls.Count > 0)
            {
                reply.Content = rest.Length == 0 ? null : rest;
                foreach (var (name, args) in calls)
                {
                    if (offered.TryGetValue(name, out var schema)) coerced += Coerce(args, schema);
                    reply.Calls.Add(new AssembledCall { Id = "call_roster_" + System.Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)), Name = name, Arguments = args.ToJsonString() });
                    recovered++;
                }
            }
        }
        if (reply.Calls.Count > MaxCalls)
        {
            dropped = reply.Calls.Count - MaxCalls;
            reply.Calls.RemoveRange(MaxCalls, dropped);
        }
        if (reply.Calls.Count > 0) reply.FinishReason = "tool_calls";
        else if (reply.FinishReason == "tool_calls") reply.FinishReason = "stop";
        return (new RepairStats(native, repaired, unreadable.Count, recovered, coerced, dropped), unreadable);
    }

    /// <summary>Turns calls that stayed unreadable after the resend into plain text, so nothing disappears without a trace.</summary>
    public static void Explain(AssembledReply reply, IEnumerable<AssembledCall> unreadable)
    {
        var sb = new StringBuilder(reply.Content ?? "");
        foreach (var c in unreadable)
        {
            var snippet = c.Arguments.Length > 200 ? c.Arguments[..200] + "..." : c.Arguments;
            sb.Append(sb.Length > 0 ? "\n" : "").Append($"[roster gateway: the call to {c.Name} was not run because its arguments are not valid JSON: {snippet}]");
        }
        reply.Content = sb.ToString();
        if (reply.Calls.Count == 0) reply.FinishReason = "stop";
    }

    // ------------------------------------------------------------ wire forms

    public static AssembledReply FromJson(JsonObject response)
    {
        var r = new AssembledReply
        {
            Id = response["id"]?.GetValue<string>() ?? "chatcmpl-roster",
            Model = response["model"]?.GetValue<string>() ?? "",
            Created = response["created"] is JsonValue c && c.TryGetValue<long>(out var created) ? created : 0,
            Usage = response["usage"]?.DeepClone(),
        };
        var choice = response["choices"] is JsonArray { Count: > 0 } choices ? choices[0] as JsonObject : null;
        var message = choice?["message"] as JsonObject;
        r.Content = message?["content"] is JsonValue cv && cv.TryGetValue<string>(out var content) ? content : null;
        r.Reasoning = message?["reasoning_content"] is JsonValue rv && rv.TryGetValue<string>(out var reasoning) ? reasoning : null;
        r.FinishReason = choice?["finish_reason"] is JsonValue fv && fv.TryGetValue<string>(out var f) ? f : null;
        foreach (var tc in (message?["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var fn = tc["function"] as JsonObject;
            r.Calls.Add(new AssembledCall
            {
                Id = tc["id"]?.GetValue<string>() ?? "",
                Name = fn?["name"]?.GetValue<string>() ?? "",
                Arguments = fn?["arguments"] switch { JsonValue v when v.TryGetValue<string>(out var a) => a, JsonObject o => o.ToJsonString(), _ => "" },
            });
        }
        return r;
    }

    public static JsonObject ToJson(AssembledReply r)
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = r.Content };
        if (r.Reasoning is not null) message["reasoning_content"] = r.Reasoning;
        if (r.Calls.Count > 0)
            message["tool_calls"] = new JsonArray([.. r.Calls.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
            })]);
        var body = new JsonObject
        {
            ["id"] = r.Id, ["object"] = "chat.completion", ["created"] = r.Created, ["model"] = r.Model,
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = r.FinishReason ?? "stop" }),
        };
        if (r.Usage is not null) body["usage"] = r.Usage.DeepClone();
        return body;
    }

    /// <summary>Assembles a streamed reply from its SSE text.</summary>
    public static AssembledReply FromSse(string sse)
    {
        var r = new AssembledReply();
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        var byIndex = new SortedDictionary<int, (StringBuilder Id, StringBuilder Name, StringBuilder Args)>();
        var first = true;
        foreach (var line in sse.Split('\n'))
        {
            var t = line.TrimEnd('\r');
            if (!t.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = t[5..].Trim();
            if (data == "[DONE]") break;
            if (TryParseNode(data) is not JsonObject chunk) continue;
            if (first)
            {
                r.Id = chunk["id"]?.GetValue<string>() ?? r.Id;
                r.Model = chunk["model"]?.GetValue<string>() ?? r.Model;
                r.Created = chunk["created"] is JsonValue cv && cv.TryGetValue<long>(out var created) ? created : 0;
                first = false;
            }
            if (chunk["usage"] is JsonObject usage) r.Usage = usage.DeepClone();
            if (chunk["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice) continue;
            if (choice["finish_reason"] is JsonValue fv && fv.TryGetValue<string>(out var f)) r.FinishReason = f;
            if (choice["delta"] is not JsonObject delta) continue;
            if (delta["content"] is JsonValue c && c.TryGetValue<string>(out var cs)) content.Append(cs);
            if (delta["reasoning_content"] is JsonValue rc && rc.TryGetValue<string>(out var rs)) reasoning.Append(rs);
            foreach (var tc in (delta["tool_calls"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var index = tc["index"] is JsonValue iv && iv.TryGetValue<int>(out var i) ? i : byIndex.Count;
                if (!byIndex.TryGetValue(index, out var acc)) byIndex[index] = acc = (new StringBuilder(), new StringBuilder(), new StringBuilder());
                if (tc["id"] is JsonValue idv && idv.TryGetValue<string>(out var id)) acc.Id.Append(id);
                if (tc["function"] is JsonObject fn)
                {
                    if (fn["name"] is JsonValue nv && nv.TryGetValue<string>(out var name)) acc.Name.Append(name);
                    if (fn["arguments"] is JsonValue av && av.TryGetValue<string>(out var args)) acc.Args.Append(args);
                }
            }
        }
        r.Content = content.Length > 0 ? content.ToString() : null;
        r.Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null;
        foreach (var (_, acc) in byIndex) r.Calls.Add(new AssembledCall { Id = acc.Id.ToString(), Name = acc.Name.ToString(), Arguments = acc.Args.ToString() });
        return r;
    }

    /// <summary>The reply as an SSE stream OMA reads like any other: reasoning, content, one chunk per call, finish, usage, done.</summary>
    public static string ToSse(AssembledReply r)
    {
        var sb = new StringBuilder();
        void Chunk(JsonObject delta, string? finish)
        {
            var chunk = new JsonObject
            {
                ["id"] = r.Id, ["object"] = "chat.completion.chunk", ["created"] = r.Created, ["model"] = r.Model,
                ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish }),
            };
            sb.Append("data: ").Append(chunk.ToJsonString()).Append("\n\n");
        }
        Chunk(new JsonObject { ["role"] = "assistant", ["content"] = "" }, null);
        if (r.Reasoning is { Length: > 0 }) Chunk(new JsonObject { ["reasoning_content"] = r.Reasoning }, null);
        if (r.Content is { Length: > 0 }) Chunk(new JsonObject { ["content"] = r.Content }, null);
        for (var i = 0; i < r.Calls.Count; i++)
        {
            var c = r.Calls[i];
            Chunk(new JsonObject
            {
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["index"] = i, ["id"] = c.Id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
                }),
            }, null);
        }
        Chunk(new JsonObject(), r.Calls.Count > 0 ? "tool_calls" : r.FinishReason ?? "stop");
        if (r.Usage is not null)
            sb.Append("data: ").Append(new JsonObject { ["id"] = r.Id, ["object"] = "chat.completion.chunk", ["created"] = r.Created, ["model"] = r.Model, ["choices"] = new JsonArray(), ["usage"] = r.Usage.DeepClone() }.ToJsonString()).Append("\n\n");
        sb.Append("data: [DONE]\n\n");
        return sb.ToString();
    }
}
