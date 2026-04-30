using System.Text.Json;
using OpenMono.Tools;

namespace OpenMono.Mcp;

public sealed class McpToolAdapter : ITool
{
    public string Name { get; }
    public string Description { get; }
    public JsonElement InputSchema { get; }
    public bool IsConcurrencySafe => true;
    public bool IsReadOnly => false;

    private readonly McpClient _client;
    private readonly string _mcpToolName;

    private McpToolAdapter(string name, string description, JsonElement inputSchema, McpClient client, string mcpToolName)
    {
        Name = name;
        Description = description;
        InputSchema = inputSchema;
        _client = client;
        _mcpToolName = mcpToolName;
    }

    public static McpToolAdapter FromMcpTool(JsonElement toolDef, McpClient client)
    {
        var mcpName = toolDef.GetProperty("name").GetString()!;
        var description = toolDef.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        var inputSchema = toolDef.TryGetProperty("inputSchema", out var s) ? s.Clone() : JsonDocument.Parse("""{"type":"object"}""").RootElement;

        var registryName = $"mcp__{client.ServerName}__{mcpName}";

        return new McpToolAdapter(registryName, $"[MCP:{client.ServerName}] {description}", inputSchema, client, mcpName);
    }

    public PermissionLevel RequiredPermission(JsonElement input) => PermissionLevel.Ask;

    public async Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        if (!_client.IsRunning)
            return ToolResult.Error($"MCP server '{_client.ServerName}' is not running");

        try
        {
            var result = await _client.CallToolAsync(_mcpToolName, input, ct);

            if (result.TryGetProperty("isError", out var isErr) && isErr.GetBoolean())
            {
                var errContent = ExtractTextContent(result);
                return ToolResult.Error(errContent);
            }

            var textPreview = ExtractTextContent(result);

            var payload = new McpResponsePayload(
                RawResponse: result.Clone(),
                ServerName: _client.ServerName,
                ToolName: _mcpToolName,
                ContentItems: ExtractContentItems(result));

            return ToolResult.SuccessWithPayload(textPreview, payload);
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"MCP tool call failed: {ex.Message}");
        }
    }

    private static List<McpContentItem> ExtractContentItems(JsonElement result)
    {
        var items = new List<McpContentItem>();

        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var t) ? t.GetString() ?? "unknown" : "unknown";
                var text = item.TryGetProperty("text", out var txt) ? txt.GetString() : null;
                var data = item.TryGetProperty("data", out var d) ? d.Clone() : (JsonElement?)null;

                items.Add(new McpContentItem(type, text, data));
            }
        }

        return items;
    }

    private static string ExtractTextContent(JsonElement result)
    {
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            var texts = content.EnumerateArray()
                .Where(c => c.TryGetProperty("type", out var t) && t.GetString() == "text")
                .Select(c => c.TryGetProperty("text", out var txt) ? txt.GetString() ?? "" : "")
                .ToList();

            if (texts.Count > 0) return string.Join('\n', texts);
        }

        return result.ToString();
    }
}

public sealed record McpResponsePayload(
    JsonElement RawResponse,
    string ServerName,
    string ToolName,
    IReadOnlyList<McpContentItem> ContentItems);

public sealed record McpContentItem(
    string Type,
    string? Text,
    JsonElement? Data);
