// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Omabot.Common;
using Omabot.Db;

namespace Omabot.Api;

/// <summary>Who is calling a roster tool. The bot comes from the socket the call arrived on, never from the request.</summary>
public sealed record ToolCaller(string Bot, string? Routine, string? Assignment);

public sealed record ToolResult(bool Ok, string Text, JsonNode? Data = null, int Status = 200)
{
    public static ToolResult Fail(string text, int status = 400) => new(false, text, null, status);
    public JsonObject ToJson() => new() { ["ok"] = Ok, ["text"] = Text, ["data"] = Data?.DeepClone() };
}

public interface IRosterTool
{
    string Name { get; }
    string Description { get; }
    JsonObject InputSchema { get; }
    Task<ToolResult> CallAsync(ToolCaller caller, JsonObject args, CancellationToken ct);
}

/// <summary>A tool defined by a lambda.</summary>
public sealed class RosterTool(string name, string description, JsonObject schema, Func<ToolCaller, JsonObject, CancellationToken, Task<ToolResult>> call) : IRosterTool
{
    public string Name => name;
    public string Description => description;
    public JsonObject InputSchema => schema;
    public Task<ToolResult> CallAsync(ToolCaller caller, JsonObject args, CancellationToken ct) => call(caller, args, ct);

    public static JsonObject Schema(params (string Name, string Type, string Desc, bool Required)[] props)
    {
        var p = new JsonObject();
        foreach (var x in props) p[x.Name] = new JsonObject { ["type"] = x.Type, ["description"] = x.Desc };
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = p,
            ["required"] = new JsonArray([.. props.Where(x => x.Required).Select(x => (JsonNode)x.Name)]),
        };
    }
}

/// <summary>
/// Per-turn capability tokens (plan 8, T12). A token is minted when the
/// shim claims it during a turn the roster started (turn_context row), is
/// bound to that turn's start time and stops working when the turn ends or
/// another turn starts. Tokens live only in memory.
/// </summary>
public sealed class ShimTokens(RosterDb db, IClock clock)
{
    private readonly ConcurrentDictionary<string, (string Hash, string TurnSetAt)> _tokens = new(StringComparer.Ordinal);

    private (string SetAt, string? Routine, string? Assignment)? ActiveTurn(string bot)
    {
        var row = db.Query("SELECT COALESCE(owner, set_at), expires_at, routine, assignment FROM turn_context WHERE bot=$b;",
            r => (r.GetString(0), r.GetString(1), RosterDb.Str(r, "routine"), RosterDb.Str(r, "assignment")), ("$b", bot)).FirstOrDefault();
        if (row.Item1 is null) return null;
        if (DateTimeOffset.Parse(row.Item2, System.Globalization.CultureInfo.InvariantCulture) <= clock.UtcNow) return null;
        return (row.Item1, row.Item3, row.Item4);
    }

    /// <summary>The turn identity (lease owner) a token is bound to, for tests and tools.</summary>
    public string? CurrentTurn(string bot) => ActiveTurn(bot)?.SetAt;

    public string? Claim(string bot)
    {
        var turn = ActiveTurn(bot);
        if (turn is null) return null;
        var token = "omt_" + SecureFile.NewToken(24);
        _tokens[bot] = (SecureFile.Sha256Hex(token), turn.Value.SetAt);
        Redactor.Shared.AddKnownSecret(token);
        return token;
    }

    /// <summary>Valid only for this bot, during the same turn it was claimed in.</summary>
    public bool Check(string bot, string? token, out string? routine) => Check(bot, token, out routine, out _);

    /// <summary>Also gives the assignment the turn works on, from the roster's own turn record.</summary>
    public bool Check(string bot, string? token, out string? routine, out string? assignment)
    {
        routine = null;
        assignment = null;
        if (string.IsNullOrEmpty(token) || !_tokens.TryGetValue(bot, out var t)) return false;
        if (!SecureFile.FixedTimeEquals(t.Hash, SecureFile.Sha256Hex(token))) return false;
        var turn = ActiveTurn(bot);
        if (turn is null || turn.Value.SetAt != t.TurnSetAt)
        {
            _tokens.TryRemove(bot, out _);
            return false;
        }
        routine = turn.Value.Routine;
        assignment = turn.Value.Assignment;
        return true;
    }
}

/// <summary>The roster tools a bot can call through the MCP shim, filtered per bot.</summary>
public sealed class RosterToolHost
{
    private readonly Dictionary<string, IRosterTool> _tools = new(StringComparer.Ordinal);
    private readonly Func<string, IReadOnlyCollection<string>>? _denied;
    private readonly Func<string, string, bool>? _allowed;

    public RosterToolHost(Func<string, IReadOnlyCollection<string>> deniedForBot) => _denied = deniedForBot;

    /// <summary>Allow-list form: a tool is served to a bot only when <paramref name="isAllowed"/> says so for (bot, tool).</summary>
    public RosterToolHost(Func<string, string, bool> isAllowed) => _allowed = isAllowed;

    public void Add(IRosterTool tool) => _tools[tool.Name] = tool;

    public IReadOnlyList<IRosterTool> For(string bot)
    {
        if (_allowed is not null) return [.. _tools.Values.Where(t => _allowed(bot, t.Name)).OrderBy(t => t.Name, StringComparer.Ordinal)];
        var denied = _denied!(bot);
        return [.. _tools.Values.Where(t => !denied.Contains(t.Name) && !denied.Contains("mcp__roster__" + t.Name)).OrderBy(t => t.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The roster's own check of a bot's roster tools: only what the team file
    /// allows, minus what it denies. memory_read (revision lookups) comes with
    /// any memory tool. Unknown bots get nothing.
    /// </summary>
    /// <summary>Roster tools the shim uses itself; never shown to the model.</summary>
    public static readonly string[] InternalTools = ["desktop_endpoint"];

    public static bool BotMayUse(Team.BotSpec? spec, string tool)
    {
        if (spec is null) return false;
        if (tool == "desktop_endpoint") return spec.Desktop != "off";
        bool Allowed(string t) => spec.ToolsAllow.Contains("mcp__roster__" + t) && !spec.ToolsDeny.Contains("mcp__roster__" + t);
        if (tool == "memory_read") return new[] { "memory_update", "memory_forget", "memory_read_topic", "memory_load" }.Any(Allowed);
        return Allowed(tool);
    }

    public JsonArray ListJson(string bot)
        => new([.. For(bot).Where(t => !InternalTools.Contains(t.Name)).Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = t.InputSchema.DeepClone() })]);

    /// <summary>Where refused calls are recorded (a bot asking for a tool it does not have, or one that does not exist).</summary>
    public Db.AuditLog? Audit { get; set; }

    public async Task<ToolResult> CallAsync(ToolCaller caller, string name, JsonObject args, CancellationToken ct)
    {
        var tool = For(caller.Bot).FirstOrDefault(t => t.Name == name);
        if (tool is null)
        {
            var shown = name.Length > 64 ? name[..64] : name;
            Audit?.Write("bot:" + caller.Bot, "tool.refused", shown, _tools.ContainsKey(name) ? "not on this bot's list" : "no such roster tool");
            return ToolResult.Fail($"No roster tool \"{shown}\" for {caller.Bot}.", 404);
        }
        try
        {
            var r = await tool.CallAsync(caller, args, ct).ConfigureAwait(false);
            return r with { Text = Redactor.Shared.Redact(r.Text) };
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            return ToolResult.Fail(Redactor.Shared.Redact(ex.Message));
        }
    }

    public static string? Str(JsonObject a, string k) => a[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
