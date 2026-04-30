using System.Text.Json;
using OpenMono.Tools;

namespace OpenMono.Mcp;

public sealed record McpServerConfig
{
    public required string Name { get; init; }
    public required string Command { get; init; }
    public string[]? Args { get; init; }
    public Dictionary<string, string>? Env { get; init; }
    public string? WorkingDirectory { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed class McpServerManager : IDisposable
{
    private readonly List<McpClient> _clients = [];
    private readonly Action<string>? _warn;

    public McpServerManager(Action<string>? warn = null)
    {
        _warn = warn;
    }

    public async Task InitializeAsync(
        IEnumerable<McpServerConfig> servers,
        ToolRegistry toolRegistry,
        CancellationToken ct)
    {
        foreach (var config in servers.Where(s => s.Enabled))
        {
            try
            {
                var client = await McpClient.ConnectAsync(config, ct);
                _clients.Add(client);

                var toolsResult = await client.ListToolsAsync(ct);
                if (toolsResult.TryGetProperty("tools", out var tools))
                {
                    foreach (var tool in tools.EnumerateArray())
                    {
                        var adapter = McpToolAdapter.FromMcpTool(tool, client);
                        toolRegistry.Register(adapter);
                    }
                }

                _warn?.Invoke($"MCP: Connected to {config.Name} — {toolsResult.GetProperty("tools").GetArrayLength()} tools");
            }
            catch (Exception ex)
            {
                _warn?.Invoke($"MCP: Failed to connect to {config.Name}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        foreach (var client in _clients)
            client.Dispose();
        _clients.Clear();
    }
}
