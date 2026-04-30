using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace OpenMono.Mcp;

public sealed class McpClient : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private int _requestId;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string ServerName { get; }
    public bool IsRunning => !_process.HasExited;

    private McpClient(Process process, string serverName)
    {
        _process = process;
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;
        ServerName = serverName;
    }

    public static async Task<McpClient> ConnectAsync(McpServerConfig config, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = config.Command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = config.WorkingDirectory ?? Directory.GetCurrentDirectory(),
        };

        foreach (var arg in config.Args ?? [])
            psi.ArgumentList.Add(arg);

        foreach (var (key, value) in config.Env ?? [])
            psi.Environment[key] = value;

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start MCP server: {config.Command}");

        var client = new McpClient(process, config.Name);

        var initResult = await client.SendRequestAsync("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "OpenMono.ai", version = "0.1.0" }
        }, ct);

        await client.SendNotificationAsync("notifications/initialized", new { }, ct);

        return client;
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
        await _lock.WaitAsync(ct);
        try
        {
            var id = Interlocked.Increment(ref _requestId);
            var request = new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params,
            };

            var json = JsonSerializer.Serialize(request);
            await _stdin.WriteLineAsync(json.AsMemory(), ct);
            await _stdin.FlushAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                var line = await _stdout.ReadLineAsync(ct);
                if (line is null) throw new InvalidOperationException("MCP server closed connection");

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("id", out var responseId)) continue;
                    if (responseId.GetInt32() != id) continue;

                    if (root.TryGetProperty("error", out var error))
                    {
                        var errMsg = error.TryGetProperty("message", out var msg) ? msg.GetString() : "Unknown MCP error";
                        throw new InvalidOperationException($"MCP error: {errMsg}");
                    }

                    if (root.TryGetProperty("result", out var result))
                        return result.Clone();

                    return default;
                }
                catch (JsonException) { continue; }
            }

            throw new OperationCanceledException();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SendNotificationAsync(string method, object @params, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var notification = new { jsonrpc = "2.0", method, @params };
            var json = JsonSerializer.Serialize(notification);
            await _stdin.WriteLineAsync(json.AsMemory(), ct);
            await _stdin.FlushAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch { }
        _process.Dispose();
        _lock.Dispose();
    }
}
