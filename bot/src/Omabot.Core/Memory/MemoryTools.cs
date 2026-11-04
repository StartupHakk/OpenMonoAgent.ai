// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text.Json.Nodes;
using Omabot.Api;

namespace Omabot.Memory;

/// <summary>The memory tools (plan 7.3): memory_read, memory_update, memory_read_topic, memory_forget.</summary>
public static class MemoryTools
{
    public static void Register(RosterToolHost host, Func<string, MemoryStore?> storeFor)
    {
        MemoryStore S(ToolCaller c) => storeFor(c.Bot) ?? throw new InvalidOperationException("Memory is off for this bot.");

        host.Add(new RosterTool("memory_read", "Read MEMORY.md (or a topic) with its revision. Pass that rev to memory_update or memory_forget.",
            RosterTool.Schema(("topic", "string", "Optional topic name; empty for MEMORY.md", false)),
            (c, a, _) =>
            {
                var s = S(c);
                var topic = RosterToolHost.Str(a, "topic");
                var rel = string.IsNullOrWhiteSpace(topic) ? MemoryStore.MemoryFile : MemoryStore.TopicFile(topic) ?? throw new ArgumentException("Bad topic name.");
                var (text, rev, unsafePath) = s.Read(rel);
                if (unsafePath) return Task.FromResult(ToolResult.Fail("That memory file is a link; the roster will not read it.", 403));
                return Task.FromResult(new ToolResult(true, text ?? "(empty)", new JsonObject { ["rev"] = rev, ["file"] = rel }));
            }));

        host.Add(new RosterTool("memory_update", "Save one fact to memory (MEMORY.md, or a topic file). One fact per call, at most 1,000 characters.",
            RosterTool.Schema(("fact", "string", "The fact, in one or a few lines", true), ("topic", "string", "Optional topic file", false),
                ("until", "string", "Optional last day it matters, YYYY-MM-DD", false), ("rev", "string", "Revision from memory_read", true)),
            (c, a, _) =>
            {
                var s = S(c);
                DateOnly? until = null;
                if (RosterToolHost.Str(a, "until") is { Length: > 0 } u)
                    until = DateOnly.TryParseExact(u, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : throw new FormatException("until must be YYYY-MM-DD.");
                var source = c.Routine is null ? "from chat" : $"from routine \"{c.Routine}\"";
                var r = s.AddEntry(RosterToolHost.Str(a, "fact") ?? "", source, until, NullIfEmpty(RosterToolHost.Str(a, "topic")), RosterToolHost.Str(a, "rev"), "bot tool", "memory_update");
                return Task.FromResult(Wrap(r));
            }));

        host.Add(new RosterTool("memory_read_topic", "Read one topic file from memory/.",
            RosterTool.Schema(("topic", "string", "Topic name", true)),
            (c, a, _) =>
            {
                var rel = MemoryStore.TopicFile(RosterToolHost.Str(a, "topic") ?? "") ?? throw new ArgumentException("Bad topic name.");
                var (text, rev, unsafePath) = S(c).Read(rel);
                if (unsafePath) return Task.FromResult(ToolResult.Fail("That topic file is a link; the roster will not read it.", 403));
                return Task.FromResult(text is null ? ToolResult.Fail("No such topic.", 404) : new ToolResult(true, text, new JsonObject { ["rev"] = rev, ["file"] = rel }));
            }));

        host.Add(new RosterTool("memory_forget", "Remove a fact from memory so it is never captured again.",
            RosterTool.Schema(("fact", "string", "The fact text to remove", true), ("topic", "string", "Optional topic file", false), ("rev", "string", "Revision from memory_read", true)),
            (c, a, _) => Task.FromResult(Wrap(S(c).Forget(RosterToolHost.Str(a, "fact") ?? "", NullIfEmpty(RosterToolHost.Str(a, "topic")), RosterToolHost.Str(a, "rev"), "bot tool")))));
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public static ToolResult Wrap(MemoryWriteResult r)
    {
        var data = new JsonObject { ["status"] = r.Status, ["rev"] = r.Rev };
        if (r.Moved is { Count: > 0 } m) data["moved"] = new JsonArray([.. m.Select(x => (JsonNode)x)]);
        return new ToolResult(r.Ok, r.Message, data, r.HttpStatus);
    }
}
