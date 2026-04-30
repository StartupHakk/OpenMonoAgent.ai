using System.Text.Json;
using OpenMono.Lsp;

namespace OpenMono.Tools;

public sealed class LspTool : ToolBase
{
    public override string Name => "Lsp";
    public override string Description => "Query a language server for code intelligence: hover info, go-to-definition, find references.";
    public override bool IsConcurrencySafe => true;
    public override bool IsReadOnly => true;
    public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

    private readonly LspServerManager _lspManager;

    public LspTool(LspServerManager lspManager)
    {
        _lspManager = lspManager;
    }

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddEnum("action", "The LSP action to perform", "hover", "definition", "references")
        .AddString("file_path", "Absolute path to the file")
        .AddInteger("line", "Line number (0-based)")
        .AddInteger("character", "Column number (0-based)")
        .Require("action", "file_path", "line", "character");

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var action = input.GetProperty("action").GetString()!;
        var filePath = Path.GetFullPath(input.GetProperty("file_path").GetString()!, context.WorkingDirectory);
        var line = input.GetProperty("line").GetInt32();
        var character = input.GetProperty("character").GetInt32();

        var client = await _lspManager.GetClientAsync(filePath, ct);
        if (client is null)
            return ToolResult.Error($"No language server available for {Path.GetExtension(filePath)} files");

        try
        {
            return action switch
            {
                "hover" => await HandleHoverAsync(client, filePath, line, character, ct),
                "definition" => await HandleDefinitionAsync(client, filePath, line, character, ct),
                "references" => await HandleReferencesAsync(client, filePath, line, character, ct),
                _ => ToolResult.Error($"Unknown LSP action: {action}. Use: hover, definition, references"),
            };
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"LSP query failed: {ex.Message}");
        }
    }

    private static async Task<ToolResult> HandleHoverAsync(
        LspClient client, string filePath, int line, int character, CancellationToken ct)
    {
        var result = await client.HoverAsync(filePath, line, character, ct);
        return result is not null
            ? ToolResult.Success($"Hover at {filePath}:{line + 1}:{character + 1}\n\n{result}")
            : ToolResult.Success("No hover information available at this position.");
    }

    private static async Task<ToolResult> HandleDefinitionAsync(
        LspClient client, string filePath, int line, int character, CancellationToken ct)
    {
        var locations = await client.DefinitionAsync(filePath, line, character, ct);
        if (locations.Count == 0)
            return ToolResult.Success("No definition found at this position.");

        var output = locations.Select(l => $"  {l}").ToList();
        return ToolResult.Success($"Definition(s):\n{string.Join('\n', output)}");
    }

    private static async Task<ToolResult> HandleReferencesAsync(
        LspClient client, string filePath, int line, int character, CancellationToken ct)
    {
        var locations = await client.ReferencesAsync(filePath, line, character, ct);
        if (locations.Count == 0)
            return ToolResult.Success("No references found at this position.");

        var output = locations.Select(l => $"  {l}").ToList();
        return ToolResult.Success($"{locations.Count} reference(s):\n{string.Join('\n', output)}");
    }
}
