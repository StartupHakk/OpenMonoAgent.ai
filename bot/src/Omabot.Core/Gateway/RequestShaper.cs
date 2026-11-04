// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Omabot.Common;

namespace Omabot.Gateway;

public sealed record ShapingResult(int ToolsTrimmed, int Spilled, int? MaxTokensCap, string? RepeatNote);

/// <summary>
/// Request-side local-model settings (plan 7.8): a tool count limit (hard
/// ceiling 128), output tokens capped at a quarter of the model's context,
/// long tool results spilled to a private file in the bot's data folder with
/// a head, a tail and the path left in context, and a short note when the
/// last three tool calls were identical with identical results. Nothing is
/// blocked; OMA's own doom loop tiers still apply inside the agent.
/// </summary>
public sealed class RequestShaper(int toolLimit, int spillBytes, Func<string, (string HostDir, string ContainerDir)?> spillDirs)
{
    public const int HardToolCeiling = 128;
    public const int Window = 10;
    public const int Streak = 3;

    public ShapingResult Apply(JsonObject body, string slug, int? contextSize)
    {
        var trimmed = 0;
        var limit = Math.Clamp(toolLimit, 1, HardToolCeiling);
        if (body["tools"] is JsonArray tools && tools.Count > limit)
        {
            trimmed = tools.Count - limit;
            while (tools.Count > limit) tools.RemoveAt(tools.Count - 1);
        }

        int? cap = null;
        if (contextSize is > 0 and var ctx)
        {
            var quarter = ctx / 4;
            foreach (var key in new[] { "max_tokens", "max_completion_tokens" })
            {
                if (body[key] is JsonValue v && v.TryGetValue<long>(out var asked) && asked > quarter)
                {
                    body[key] = quarter;
                    cap = quarter;
                }
            }
        }

        var spilled = 0;
        var messages = body["messages"] as JsonArray ?? [];
        if (spillBytes > 0 && spillDirs(slug) is { } dirs)
        {
            foreach (var m in messages.OfType<JsonObject>())
            {
                if (m["role"]?.GetValue<string>() != "tool" || m["content"] is not JsonValue cv || !cv.TryGetValue<string>(out var text)) continue;
                if (Encoding.UTF8.GetByteCount(text) <= spillBytes) continue;
                m["content"] = Spill(text, dirs.HostDir, dirs.ContainerDir);
                spilled++;
            }
        }

        var note = RepeatNote(messages);
        if (note is not null && messages.Count > 0 && messages[^1] is JsonObject last && last["content"] is JsonValue lv && lv.TryGetValue<string>(out var lastText))
            last["content"] = lastText + "\n\n[roster note] " + note;
        return new ShapingResult(trimmed, spilled, cap, note);
    }

    /// <summary>Writes the full result 0600 once (named by its hash) and returns the head, a pointer and the tail.</summary>
    public string Spill(string text, string hostDir, string containerDir)
    {
        var safe = Redactor.Shared.Redact(text);
        var name = SecureFile.Sha256Hex(safe)[..16] + ".txt";
        SecureFile.EnsureDirNoClobber(hostDir);
        var file = Path.Combine(hostDir, name);
        if (!File.Exists(file)) SecureFile.WriteIfMissing(file, safe);
        var headLen = Math.Min(safe.Length, Math.Max(256, spillBytes / 4));
        var tailLen = Math.Min(safe.Length - headLen, Math.Max(128, spillBytes / 16));
        return safe[..headLen]
               + $"\n\n[roster gateway: this tool result was {Encoding.UTF8.GetByteCount(text)} bytes, so the middle is left out here. The full text is in {containerDir.TrimEnd('/')}/{name}; read it with FileRead if you need it.]\n\n"
               + safe[^tailLen..];
    }

    /// <summary>
    /// The note for the next request when the last three tool calls in the
    /// history were the same call with the same result, or null.
    /// </summary>
    public static string? RepeatNote(JsonArray messages)
    {
        var calls = new List<(string Id, string Name, string Args)>();
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in messages.OfType<JsonObject>())
        {
            var role = m["role"]?.GetValue<string>();
            if (role == "assistant" && m["tool_calls"] is JsonArray tcs)
            {
                foreach (var tc in tcs.OfType<JsonObject>())
                {
                    var fn = tc["function"] as JsonObject;
                    var args = fn?["arguments"] switch { JsonValue v when v.TryGetValue<string>(out var a) => a, JsonObject o => o.ToJsonString(), _ => "" };
                    calls.Add((tc["id"]?.GetValue<string>() ?? "", fn?["name"]?.GetValue<string>() ?? "", Canonical(args)));
                }
            }
            else if (role == "tool" && m["tool_call_id"] is JsonValue idv && idv.TryGetValue<string>(out var id))
            {
                results[id] = m["content"] switch { JsonValue v when v.TryGetValue<string>(out var c) => c, JsonNode n => n.ToJsonString(), _ => "" };
            }
        }
        var recent = calls.TakeLast(Window).ToList();
        if (recent.Count < Streak) return null;
        var last = recent.TakeLast(Streak).ToList();
        if (last.Any(c => !results.ContainsKey(c.Id))) return null;
        var first = last[0];
        if (last.Any(c => c.Name != first.Name || c.Args != first.Args || results[c.Id] != results[first.Id])) return null;
        return $"You called {first.Name} with the same arguments {Streak} times and got the same result. Try something different or stop.";
    }

    private static string Canonical(string args)
    {
        try
        {
            return Approvals.ApprovalStore.Canonical(JsonNode.Parse(args));
        }
        catch (JsonException)
        {
            return args.Trim();
        }
    }
}
