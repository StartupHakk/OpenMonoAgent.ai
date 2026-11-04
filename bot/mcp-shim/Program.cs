// SPDX-License-Identifier: AGPL-3.0-or-later
// The roster MCP shim: a stdio MCP server inside each bot container. It
// lists the roster tools for this bot and forwards calls to the roster over
// the bot's own unix socket, with a per-turn token it claims from the roster.
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Omabot.McpShim;

public static class Program
{
    public static async Task<int> Main()
    {
        var socket = Environment.GetEnvironmentVariable("ROSTER_SOCKET") ?? "/omabot/sock/roster.sock";
        using var http = ShimClient.ForSocket(socket);
        var shim = new ShimClient(http);
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        string? line;
        while ((line = await stdin.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            if (line.Trim().Length == 0) continue;
            var reply = await shim.HandleAsync(line).ConfigureAwait(false);
            if (reply is not null) await stdout.WriteLineAsync(reply).ConfigureAwait(false);
        }
        return 0;
    }
}

/// <summary>MCP message handling, kept free of stdio so tests can drive it.</summary>
public sealed class ShimClient(HttpClient http)
{
    public const string ProtocolVersion = "2024-11-05";
    private string? _token;

    public static HttpClient ForSocket(string path)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await s.ConnectAsync(new UnixDomainSocketEndPoint(path), ct).ConfigureAwait(false);
                return new NetworkStream(s, true);
            },
        };
        return new HttpClient(handler) { BaseAddress = new Uri("http://roster"), Timeout = TimeSpan.FromMinutes(10) };
    }

    public async Task<string?> HandleAsync(string line)
    {
        JsonObject msg;
        try { msg = JsonNode.Parse(line) as JsonObject ?? throw new FormatException(); }
        catch (Exception) { return Error(null, -32700, "Parse error"); }
        var id = msg["id"]?.DeepClone();
        var method = msg["method"]?.GetValue<string>() ?? "";
        if (id is null) return null; // notifications need no reply
        try
        {
            switch (method)
            {
                case "initialize":
                    return Result(id, new JsonObject
                    {
                        ["protocolVersion"] = ProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "roster", ["version"] = "1" },
                    });
                case "ping":
                    return Result(id, new JsonObject());
                case "tools/list":
                    var list = JsonNode.Parse(await http.GetStringAsync("/v1/tools").ConfigureAwait(false))!["tools"]!.DeepClone();
                    return Result(id, new JsonObject { ["tools"] = list });
                case "tools/call":
                    var p = msg["params"] as JsonObject ?? [];
                    var name = p["name"]?.GetValue<string>() ?? "";
                    var args = p["arguments"] as JsonObject ?? [];
                    var (ok, text) = await CallToolAsync(name, args).ConfigureAwait(false);
                    return Result(id, new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                        ["isError"] = !ok,
                    });
                default:
                    return Error(id, -32601, "Method not found: " + method);
            }
        }
        catch (HttpRequestException ex)
        {
            return Error(id, -32000, "The roster is not reachable: " + ex.Message);
        }
    }

    /// <summary>
    /// Calls one roster tool. Writes that need a revision get one read first
    /// when the bot left it out, and a conflict rereads and retries once.
    /// </summary>
    public async Task<(bool Ok, string Text)> CallToolAsync(string name, JsonObject args)
    {
        var needsRev = name is "memory_update" or "memory_forget";
        if (needsRev && args["rev"] is null) args["rev"] = await RevAsync(args).ConfigureAwait(false);
        var (status, body) = await PostAsync(name, args).ConfigureAwait(false);
        if (status == 409 && needsRev)
        {
            args["rev"] = await RevAsync(args).ConfigureAwait(false);
            (status, body) = await PostAsync(name, args).ConfigureAwait(false);
        }
        var text = body?["text"]?.GetValue<string>() ?? body?["message"]?.GetValue<string>() ?? $"roster status {status}";
        var data = body?["data"];
        if (data is JsonObject d && d.Count > 0) text += "\n" + d.ToJsonString();
        return (body?["ok"]?.GetValue<bool>() == true, text);
    }

    private async Task<string> RevAsync(JsonObject args)
    {
        var (_, body) = await PostAsync("memory_read", new JsonObject { ["topic"] = args["topic"]?.DeepClone() }).ConfigureAwait(false);
        return body?["data"]?["rev"]?.GetValue<string>() ?? "absent";
    }

    private async Task<(int Status, JsonObject? Body)> PostAsync(string name, JsonObject args)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_token is null && !await ClaimAsync().ConfigureAwait(false))
                return (409, new JsonObject { ["ok"] = false, ["text"] = "Roster tools work only during a turn the roster started." });
            using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/tools/call")
            {
                Content = new StringContent(new JsonObject { ["name"] = name, ["arguments"] = args.DeepClone() }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var resp = await http.SendAsync(req).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if ((int)resp.StatusCode == 401) { _token = null; continue; } // the turn changed; claim again once
            JsonObject? body = null;
            try { body = JsonNode.Parse(text) as JsonObject; } catch (Exception) { }
            return ((int)resp.StatusCode, body);
        }
        return (401, new JsonObject { ["ok"] = false, ["text"] = "The roster refused the turn token." });
    }

    private async Task<bool> ClaimAsync()
    {
        using var resp = await http.PostAsync("/v1/turn/claim", null).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return false;
        _token = JsonNode.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false))?["token"]?.GetValue<string>();
        return _token is not null;
    }

    private static string Result(JsonNode id, JsonNode result) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();

    private static string Error(JsonNode? id, int code, string message)
        => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();
}
