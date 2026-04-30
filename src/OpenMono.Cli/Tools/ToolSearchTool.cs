using System.Text;
using System.Text.Json;
using OpenMono.Permissions;

namespace OpenMono.Tools;

public sealed class ToolSearchTool : ToolBase
{
    public override string Name => "ToolSearch";

    public override string Description =>
        "Fetch schemas for deferred tools. Use 'tools' to get specific tool schemas, " +
        "'query' to search by keyword, or 'list_deferred' to see all available deferred tools.";

    public override bool IsConcurrencySafe => true;
    public override bool IsReadOnly => true;

    public override bool IsDeferred => false;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddArray("tools", "Specific tool names to fetch schemas for", new { type = "string" })
        .AddString("query", "Search query to find tools by name or description")
        .AddBoolean("list_deferred", "If true, list all deferred tools with brief descriptions")
        .AddInteger("max_results", "Maximum number of search results (default: 10)", minimum: 1, maximum: 50);

    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input) => [];

    protected override Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var registry = context.ToolRegistry;
        if (registry is null)
            return Task.FromResult(ToolResult.Error("ToolRegistry not available in context"));

        var sb = new StringBuilder();

        if (input.TryGetProperty("tools", out var toolsArray) && toolsArray.ValueKind == JsonValueKind.Array)
        {
            var toolNames = toolsArray.EnumerateArray()
                .Select(e => e.GetString())
                .Where(n => n is not null)
                .Cast<string>()
                .ToList();

            if (toolNames.Count == 0)
                return Task.FromResult(ToolResult.InvalidInput(
                    "No tool names provided", "Provide at least one tool name in the 'tools' array"));

            var found = new List<string>();
            var notFound = new List<string>();

            foreach (var name in toolNames)
            {
                var tool = registry.Resolve(name);
                if (tool is not null)
                    found.Add(name);
                else
                    notFound.Add(name);
            }

            if (found.Count > 0)
            {
                var schemas = registry.BuildToolDefinitionsFor(found);
                sb.AppendLine($"## Tool Schemas ({found.Count} found)");
                sb.AppendLine();
                sb.AppendLine("```json");
                sb.AppendLine(JsonSerializer.Serialize(schemas, new JsonSerializerOptions { WriteIndented = true }));
                sb.AppendLine("```");
            }

            if (notFound.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"**Not found:** {string.Join(", ", notFound)}");
            }

            return Task.FromResult(ToolResult.Success(sb.ToString()));
        }

        if (input.TryGetProperty("list_deferred", out var listDeferred) &&
            listDeferred.ValueKind == JsonValueKind.True)
        {
            var deferred = registry.ListDeferredTools();

            if (deferred.Count == 0)
            {
                return Task.FromResult(ToolResult.Success("No deferred tools available."));
            }

            sb.AppendLine($"## Deferred Tools ({deferred.Count} available)");
            sb.AppendLine();
            sb.AppendLine("Use `{ \"tools\": [\"ToolName\"] }` to fetch the full schema.");
            sb.AppendLine();

            foreach (var (name, desc) in deferred)
            {
                sb.AppendLine($"- **{name}**: {desc}");
            }

            return Task.FromResult(ToolResult.Success(sb.ToString()));
        }

        if (input.TryGetProperty("query", out var queryProp) &&
            queryProp.ValueKind == JsonValueKind.String)
        {
            var query = queryProp.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(query))
                return Task.FromResult(ToolResult.InvalidInput(
                    "Empty search query", "Provide a non-empty search query"));

            var maxResults = input.TryGetProperty("max_results", out var mr) ? mr.GetInt32() : 10;
            maxResults = Math.Clamp(maxResults, 1, 50);

            var results = registry.SearchTools(query, includeActive: false, maxResults: maxResults);

            if (results.Count == 0)
            {

                var activeMatches = registry.SearchTools(query, includeActive: true, maxResults: 3);
                if (activeMatches.Count > 0)
                {
                    sb.AppendLine($"No deferred tools match '{query}'.");
                    sb.AppendLine();
                    sb.AppendLine("These **active tools** (already in your context) match:");
                    foreach (var t in activeMatches)
                        sb.AppendLine($"- {t.Name}");
                }
                else
                {
                    sb.AppendLine($"No tools match '{query}'.");
                }

                return Task.FromResult(ToolResult.Success(sb.ToString()));
            }

            sb.AppendLine($"## Search Results for '{query}' ({results.Count} deferred tools)");
            sb.AppendLine();

            foreach (var tool in results)
            {
                var desc = tool.Description.Length > 80
                    ? tool.Description[..80] + "..."
                    : tool.Description;
                sb.AppendLine($"- **{tool.Name}**: {desc}");
            }

            sb.AppendLine();
            sb.AppendLine("Use `{ \"tools\": [\"ToolName\"] }` to fetch full schemas.");

            return Task.FromResult(ToolResult.Success(sb.ToString()));
        }

        return Task.FromResult(ToolResult.InvalidInput(
            "No search mode specified",
            "Provide 'tools' (array of names), 'query' (search string), or 'list_deferred' (true)"));
    }
}
