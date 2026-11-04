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

    private (string SetAt, string? Routine)? ActiveTurn(string bot)
    {
        var row = db.Query("SELECT set_at, expires_at, routine FROM turn_context WHERE bot=$b;", r => (r.GetString(0), r.GetString(1), RosterDb.Str(r, "routine")), ("$b", bot)).FirstOrDefault();
        if (row.Item1 is null) return null;
        if (DateTimeOffset.Parse(row.Item2, System.Globalization.CultureInfo.InvariantCulture) <= clock.UtcNow) return null;
        return (row.Item1, row.Item3);
    }

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
    public bool Check(string bot, string? token, out string? routine)
    {
        routine = null;
        if (string.IsNullOrEmpty(token) || !_tokens.TryGetValue(bot, out var t)) return false;
        if (!SecureFile.FixedTimeEquals(t.Hash, SecureFile.Sha256Hex(token))) return false;
        var turn = ActiveTurn(bot);
        if (turn is null || turn.Value.SetAt != t.TurnSetAt)
        {
            _tokens.TryRemove(bot, out _);
            return false;
        }
        routine = turn.Value.Routine;
        return true;
    }
}

/// <summary>The roster tools a bot can call through the MCP shim, filtered per bot.</summary>
public sealed class RosterToolHost
{
    private readonly Dictionary<string, IRosterTool> _tools = new(StringComparer.Ordinal);
    private readonly Func<string, IReadOnlyCollection<string>> _denied;

    public RosterToolHost(Func<string, IReadOnlyCollection<string>> deniedForBot) => _denied = deniedForBot;

    public void Add(IRosterTool tool) => _tools[tool.Name] = tool;

    public IReadOnlyList<IRosterTool> For(string bot)
    {
        var denied = _denied(bot);
        return [.. _tools.Values.Where(t => !denied.Contains(t.Name) && !denied.Contains("mcp__roster__" + t.Name)).OrderBy(t => t.Name, StringComparer.Ordinal)];
    }

    public JsonArray ListJson(string bot)
        => new([.. For(bot).Select(t => (JsonNode)new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = t.InputSchema.DeepClone() })]);

    public async Task<ToolResult> CallAsync(ToolCaller caller, string name, JsonObject args, CancellationToken ct)
    {
        var tool = For(caller.Bot).FirstOrDefault(t => t.Name == name);
        if (tool is null) return ToolResult.Fail($"No roster tool \"{name}\" for {caller.Bot}.", 404);
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
