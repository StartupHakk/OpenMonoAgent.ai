using System.Text.Json;
using System.Text.Json.Nodes;
using OpenMono.Config;

namespace OpenMono.Windows.AgentHost;

/// <summary>
/// Writes the standard OMA settings.json surface from the first run wizard
/// so behavior matches Linux and macOS. Serializes through
/// <see cref="JsonOptions"/> (snake_case: ctx_size, acp_server,
/// vision_enabled, api_key) so <see cref="ConfigLoader"/> round-trips the
/// file. Existing user-managed content (llm tuning, extra permission tools,
/// hooks, providers, MCP servers) is preserved by merging over the current
/// file instead of rewriting it wholesale. Legacy camelCase twins written by
/// the pre-M1.1 writer (inference.ctxSize, root acpServer) are removed.
/// </summary>
public static class OmaSettingsWriter
{
    public static void Write(
        string dataDirectory,
        string endpoint,
        string modelAlias,
        int ctxSize,
        bool visionEnabled,
        int acpPort,
        bool acpEnabled,
        string? apiKey = null)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "settings.json");
        var root = LoadExistingRoot(path);

        // llm: keep user tuning (temperature, top_p, ...), overwrite wizard keys.
        var llm = root["llm"] as JsonObject ?? new JsonObject();
        llm["endpoint"] = endpoint;
        llm["model"] = modelAlias;
        llm["context_size"] = ctxSize;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            llm["api_key"] = apiKey;
        }

        root["llm"] = llm;

        // inference: keep anything else present, overwrite ctx_size.
        var inference = root["inference"] as JsonObject ?? new JsonObject();
        inference["ctx_size"] = ctxSize;
        root["inference"] = inference;

        root["vision_enabled"] = visionEnabled;

        // acp_server: merge so future upstream fields survive a wizard rewrite.
        var acp = root["acp_server"] as JsonObject ?? new JsonObject();
        acp["enabled"] = acpEnabled;
        acp["port"] = acpPort;
        root["acp_server"] = acp;

        // permissions.tools.Bash: union Windows defaults with existing rules.
        var permissions = root["permissions"] as JsonObject ?? new JsonObject();
        var tools = permissions["tools"] as JsonObject ?? new JsonObject();
        tools["Bash"] = MergeToolRules(ParseToolRules(tools["Bash"]), WindowsPermissionDefaults.Defaults()["Bash"]);
        permissions["tools"] = tools;
        root["permissions"] = permissions;

        RemoveLegacyTwins(root);

        File.WriteAllText(path, root.ToJsonString(JsonOptions.Indented));
    }

    private static JsonObject LoadExistingRoot(string path)
    {
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject obj)
            {
                return obj;
            }
        }
        catch
        {
            // Malformed file: replaced below with a fresh object.
        }

        return new JsonObject();
    }

    private static ToolPermissionRules? ParseToolRules(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.Deserialize<ToolPermissionRules>(JsonOptions.Default);
        }
        catch
        {
            return null;
        }
    }

    private static JsonNode MergeToolRules(ToolPermissionRules? existing, ToolPermissionRules defaults)
    {
        var merged = new ToolPermissionRules
        {
            Allow = Union(existing?.Allow, defaults.Allow),
            Deny = Union(existing?.Deny, defaults.Deny),
            Ask = Union(existing?.Ask, defaults.Ask),
        };
        return JsonSerializer.SerializeToNode(merged, JsonOptions.Indented)!;
    }

    private static List<string> Union(List<string>? existing, List<string> defaults)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();
        foreach (var value in existing ?? [])
        {
            if (seen.Add(value))
            {
                merged.Add(value);
            }
        }

        foreach (var value in defaults)
        {
            if (seen.Add(value))
            {
                merged.Add(value);
            }
        }

        return merged;
    }

    /// <summary>
    /// Drops keys only the pre-M1.1 hand-concatenated writer produced.
    /// OMA never reads them (it reads snake_case), so they are dead weight
    /// that confuses anyone diffing the file against ConfigLoader behavior.
    /// </summary>
    private static void RemoveLegacyTwins(JsonObject root)
    {
        (root["inference"] as JsonObject)?.Remove("ctxSize");
        root.Remove("acpServer");
    }
}
