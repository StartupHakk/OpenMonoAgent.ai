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
        // The desktop server goes straight to the desktop on the internal network, never through the egress proxy.
        using var direct = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(2) };
        Func<string, Task<string?>> handle = Environment.GetEnvironmentVariable("ROSTER_MCP_MODE") == "desktop"
            ? new DesktopBridge(shim, direct).HandleAsync
            : shim.HandleAsync;
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        string? line;
        while ((line = await stdin.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            if (line.Trim().Length == 0) continue;
            var reply = await handle(line).ConfigureAwait(false);
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

    internal static string Result(JsonNode id, JsonNode result) => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();

    internal static string Error(JsonNode? id, int code, string message)
        => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();
}

/// <summary>
/// The desktop server (plan 7.7): two fixed tools, so OMA can list them at
/// start without a desktop. Inside a roster turn each call asks the roster
/// for this bot's desktop (a lease, starting it when needed) and forwards to
/// the desktop's MCP server over streamable HTTP on the internal network.
/// </summary>
public sealed class DesktopBridge(ShimClient roster, HttpClient http)
{
    private string? _url;
    private string? _session;
    private int _next;

    public async Task<string?> HandleAsync(string line)
    {
        JsonObject msg;
        try { msg = JsonNode.Parse(line) as JsonObject ?? throw new FormatException(); }
        catch (Exception) { return ShimClient.Error(null, -32700, "Parse error"); }
        var id = msg["id"]?.DeepClone();
        var method = msg["method"]?.GetValue<string>() ?? "";
        if (id is null) return null;
        try
        {
            switch (method)
            {
                case "initialize":
                    return ShimClient.Result(id, new JsonObject
                    {
                        ["protocolVersion"] = ShimClient.ProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "desktop", ["version"] = "1" },
                    });
                case "ping":
                    return ShimClient.Result(id, new JsonObject());
                case "tools/list":
                    return ShimClient.Result(id, new JsonObject { ["tools"] = Tools() });
                case "tools/call":
                    var p = msg["params"] as JsonObject ?? [];
                    var name = p["name"]?.GetValue<string>() ?? "";
                    var args = p["arguments"] as JsonObject ?? [];
                    var (ok, text) = name switch
                    {
                        "desktop_list_actions" => await ListAsync().ConfigureAwait(false),
                        "desktop_act" => await ActAsync(args["action"]?.GetValue<string>() ?? "", args["arguments"] as JsonObject ?? []).ConfigureAwait(false),
                        _ => (false, "No desktop tool " + name),
                    };
                    return ShimClient.Result(id, new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                        ["isError"] = !ok,
                    });
                default:
                    return ShimClient.Error(id, -32601, "Method not found: " + method);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _session = null;
            return ShimClient.Error(id, -32000, "The desktop is not reachable: " + ex.Message);
        }
    }

    public static JsonArray Tools() =>
    [
        new JsonObject
        {
            ["name"] = "desktop_list_actions",
            ["description"] = "List what you can do on your desktop (its actions and their arguments). Starts the desktop when needed.",
            ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        },
        new JsonObject
        {
            ["name"] = "desktop_act",
            ["description"] = "Do one action on your desktop, for example take a screenshot, click or type. Use desktop_list_actions first.",
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["action"] = new JsonObject { ["type"] = "string", ["description"] = "The action name from desktop_list_actions" },
                    ["arguments"] = new JsonObject { ["type"] = "object", ["description"] = "The action's arguments" },
                },
                ["required"] = new JsonArray("action"),
            },
        },
    ];

    private async Task<(bool, string)> ListAsync()
    {
        var (url, problem) = await EndpointAsync().ConfigureAwait(false);
        if (url is null) return (false, problem!);
        var reply = await RpcAsync(url, "tools/list", new JsonObject()).ConfigureAwait(false);
        if (reply?["error"] is JsonObject err) return (false, "The desktop refused: " + err["message"]?.GetValue<string>());
        var tools = reply?["result"]?["tools"] as JsonArray ?? [];
        var sb = new StringBuilder();
        foreach (var t in tools.OfType<JsonObject>())
            sb.Append("- ").Append(t["name"]?.GetValue<string>()).Append(": ").Append(t["description"]?.GetValue<string>()).Append(" Arguments: ").Append(t["inputSchema"]?.ToJsonString()).Append('\n');
        return (true, sb.Length == 0 ? "The desktop offers no actions." : sb.ToString());
    }

    private async Task<(bool, string)> ActAsync(string action, JsonObject arguments)
    {
        if (action.Length == 0) return (false, "action is required.");
        var (url, problem) = await EndpointAsync().ConfigureAwait(false);
        if (url is null) return (false, problem!);
        var reply = await RpcAsync(url, "tools/call", new JsonObject { ["name"] = action, ["arguments"] = arguments.DeepClone() }).ConfigureAwait(false);
        if (reply?["error"] is JsonObject err) return (false, "The desktop refused: " + err["message"]?.GetValue<string>());
        var result = reply?["result"] as JsonObject ?? [];
        var parts = (result["content"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(c => c["type"]?.GetValue<string>() == "text" ? c["text"]?.GetValue<string>() ?? "" : $"[{c["type"]?.GetValue<string>()} content, {c.ToJsonString().Length} bytes]");
        return (result["isError"]?.GetValue<bool>() != true, string.Join("\n", parts));
    }

    /// <summary>Asks the roster for this bot's desktop each time, which also keeps the lease alive.</summary>
    private async Task<(string? Url, string? Problem)> EndpointAsync()
    {
        var (ok, text) = await roster.CallToolAsync("desktop_endpoint", new JsonObject()).ConfigureAwait(false);
        if (!ok) return (null, text);
        var url = text.Split('\n')[0].Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "http" || !u.Host.StartsWith("oma-desktop-", StringComparison.Ordinal))
            return (null, "The roster gave an unexpected desktop address.");
        if (url != _url) _session = null;
        _url = url;
        return (url, null);
    }

    private async Task<JsonObject?> RpcAsync(string url, string method, JsonObject @params)
    {
        if (_session is null)
        {
            var init = await PostAsync(url, new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = ++_next, ["method"] = "initialize",
                ["params"] = new JsonObject { ["protocolVersion"] = ShimClient.ProtocolVersion, ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "oma-roster", ["version"] = "1" } },
            }).ConfigureAwait(false);
            if (init?["error"] is not null) return init;
            await PostAsync(url, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }).ConfigureAwait(false);
        }
        return await PostAsync(url, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++_next, ["method"] = method, ["params"] = @params }).ConfigureAwait(false);
    }

    /// <summary>One streamable HTTP exchange: a JSON body back, or server-sent events until the reply with our id.</summary>
    private async Task<JsonObject?> PostAsync(string url, JsonObject message)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        if (_session is not null) req.Headers.Add("Mcp-Session-Id", _session);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        if (resp.Headers.TryGetValues("Mcp-Session-Id", out var sid)) _session = sid.FirstOrDefault() ?? _session;
        if (message["id"] is null) return null;
        resp.EnsureSuccessStatusCode();
        var type = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (type == "text/event-stream")
        {
            using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync().ConfigureAwait(false));
            string? l;
            while ((l = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (!l.StartsWith("data:", StringComparison.Ordinal)) continue;
                if (JsonNode.Parse(l[5..].Trim()) is JsonObject o && o["id"]?.ToJsonString() == message["id"]!.ToJsonString()) return o;
            }
            return null;
        }
        return JsonNode.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false)) as JsonObject;
    }
}
