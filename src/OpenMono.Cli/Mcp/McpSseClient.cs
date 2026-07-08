using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenMono.Mcp;

public sealed class McpSseClient : IMcpClient
{
    private readonly HttpClient _http;
    private readonly string _messageUrl;
    private int _requestId;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();

    public string ServerName { get; }
    public bool IsRunning => !_disposeCts.IsCancellationRequested;

    private McpSseClient(HttpClient http, string messageUrl, string serverName)
    {
        _http = http;
        _messageUrl = messageUrl;
        ServerName = serverName;
    }

    public static async Task<McpSseClient> ConnectAsync(McpServerConfig config, CancellationToken ct)
    {
        var baseUri = new Uri(config.Url!);
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromMinutes(5) };

        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        // SSE handshake: GET with Accept headers
        var sseResponse = await http.GetAsync(baseUri, HttpCompletionOption.ResponseHeadersRead, ct);
        sseResponse.EnsureSuccessStatusCode();
        var stream = await sseResponse.Content.ReadAsStreamAsync(ct);
        var reader = new StreamReader(stream);

        string? messageEndpoint = null;
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;

            if (line.StartsWith("event: endpoint"))
            {
                var dataLine = await reader.ReadLineAsync(ct);
                if (dataLine != null && dataLine.StartsWith("data: "))
                {
                    messageEndpoint = dataLine[6..].Trim();
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(messageEndpoint))
            throw new InvalidOperationException("SSE handshake failed: no endpoint received");

        var client = new McpSseClient(http, messageEndpoint, config.Name);

        // Start background SSE reader
        _ = client.ReadSseLoopAsync(reader, ct);

        // Initialize via POST
        await client.SendRequestAsync("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "OpenMono.ai", version = "0.1.0" }
        }, ct);

        await client.SendNotificationAsync("notifications/initialized", new { }, ct);

        return client;
    }

    // Responses from SSE stream keyed by request ID
    private readonly Dictionary<int, TaskCompletionSource<JsonElement>> _pending = new();

    private async Task ReadSseLoopAsync(StreamReader reader, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        var token = linked.Token;

        try
        {
            string? currentEvent = null;
            string? currentData = null;

            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (line == null) break;

                if (line.StartsWith("event: "))
                {
                    currentEvent = line[7..];
                    currentData = null;
                }
                else if (line.StartsWith("data: "))
                {
                    currentData = line[6..];
                }
                else if (line == "")
                {
                    // Empty line = end of event
                    if (currentEvent == "message" && currentData != null)
                    {
                        HandleResponse(currentData);
                    }
                    currentEvent = null;
                    currentData = null;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            // SSE connection lost; fail all pending requests
        }
        finally
        {
            lock (_pending)
            {
                foreach (var tcs in _pending.Values)
                    tcs.TrySetException(new InvalidOperationException("SSE connection closed"));
                _pending.Clear();
            }
        }
    }

    private void HandleResponse(string data)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;

        if (!root.TryGetProperty("id", out var idElement)) return;
        int id;
        try { id = idElement.GetInt32(); }
        catch { return; }

        TaskCompletionSource<JsonElement>? tcs;
        lock (_pending)
        {
            if (_pending.TryGetValue(id, out tcs))
                _pending.Remove(id);
        }

        if (tcs == null) return;

        if (root.TryGetProperty("error", out var error))
        {
            var msg = error.TryGetProperty("message", out var m) ? m.GetString() : "MCP error";
            tcs.TrySetException(new InvalidOperationException(msg));
            return;
        }

        if (root.TryGetProperty("result", out var result))
            tcs.TrySetResult(result.Clone());
        else
            tcs.TrySetResult(default);
    }

    public async Task<JsonElement> ListToolsAsync(CancellationToken ct)
    {
        return await SendRequestAsync("tools/list", new { }, ct);
    }

    public async Task<JsonElement> CallToolAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        return await SendRequestAsync("tools/call", new { name, arguments }, ct);
    }

    public async Task<JsonElement> ListResourcesAsync(CancellationToken ct)
    {
        return await SendRequestAsync("resources/list", new { }, ct);
    }

    public async Task<JsonElement> ReadResourceAsync(string uri, CancellationToken ct)
    {
        return await SendRequestAsync("resources/read", new { uri }, ct);
    }

    private async Task<JsonElement> SendRequestAsync(string method, object @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _requestId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_pending)
            _pending[id] = tcs;

        try
        {
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = JsonSerializer.SerializeToNode(@params)!.AsObject()
            };

            var json = request.ToJsonString();
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
            var response = await _http.PostAsync(_messageUrl, content, linked.Token);
            response.EnsureSuccessStatusCode();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked2 = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            return await tcs.Task.WaitAsync(linked2.Token);
        }
        catch
        {
            lock (_pending)
                _pending.Remove(id);
            throw;
        }
    }

    private async Task SendNotificationAsync(string method, object @params, CancellationToken ct)
    {
        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = JsonSerializer.SerializeToNode(@params)!.AsObject()
        };

        var json = notification.ToJsonString();
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        var response = await _http.PostAsync(_messageUrl, content, linked.Token);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
        _disposeCts.Dispose();
        _http.Dispose();
        _lock.Dispose();
    }
}
