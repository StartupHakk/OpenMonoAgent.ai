using System.Text.Json;
using OpenMono.Config;

namespace OpenMono.Tools;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ITool tool)
    {
        _tools[tool.Name] = tool;
    }

    public ITool? Resolve(string name) =>
        _tools.GetValueOrDefault(name);

    public IReadOnlyCollection<ITool> All => _tools.Values;

    public IEnumerable<ITool> ActiveTools => _tools.Values.Where(t => !t.IsDeferred);

    public IEnumerable<ITool> DeferredTools => _tools.Values.Where(t => t.IsDeferred);

    public JsonElement BuildToolDefinitions()
    {

        var ordered = _tools.Values
            .Where(t => !t.IsDeferred)
            .OrderBy(t => t.Name.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(t => t.Name, StringComparer.Ordinal);

        var tools = ordered.Select(t => new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = t.InputSchema
            }
        });

        var json = JsonSerializer.Serialize(tools, JsonOptions.Default);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public JsonElement BuildToolDefinitionsFor(IEnumerable<string> toolNames)
    {
        var nameSet = new HashSet<string>(toolNames, StringComparer.OrdinalIgnoreCase);
        var tools = _tools.Values
            .Where(t => nameSet.Contains(t.Name))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = t.InputSchema
                }
            });

        var json = JsonSerializer.Serialize(tools, JsonOptions.Default);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    public IReadOnlyList<(string Name, string Description)> ListDeferredTools()
    {
        return _tools.Values
            .Where(t => t.IsDeferred)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => (t.Name, t.Description.Length > 100
                ? t.Description[..100] + "..."
                : t.Description))
            .ToList();
    }

    public IReadOnlyList<ITool> SearchTools(string query, bool includeActive = false, int maxResults = 10)
    {
        var q = query.ToLowerInvariant();
        return _tools.Values
            .Where(t => includeActive || t.IsDeferred)
            .Where(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        t.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(t => t.Name)
            .Take(maxResults)
            .ToList();
    }
}
