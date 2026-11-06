using System.Text.Json;

namespace OpenMono.Win.Launcher;

// The slice of ~/.openmono/settings.json the host launcher needs, read the
// way the bash launcher reads it with jq: missing or unreadable keys are
// simply absent. The agent container reads the full file itself through the
// ~/.openmono mount; nothing here rewrites it.
public sealed record HostSettings(
    string? LlmEndpoint,
    string? LlmModel,
    bool? VisionEnabled,
    string? WebGateway,
    string? WebSearch,
    string? WebScrape,
    bool Corrupt = false)
{
    public static readonly HostSettings Empty = new(null, null, null, null, null, null);

    public static string PathFor(string homeDir) => Path.Combine(homeDir, ".openmono", "settings.json");

    public static HostSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return Empty;
        }

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return Empty with { Corrupt = true };
        }
    }

    public static HostSettings Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new HostSettings(
                Str(root, "llm", "endpoint"),
                Str(root, "llm", "model"),
                Bool(root, "inference", "vision_enabled"),
                Str(root, "web", "gateway"),
                Str(root, "web", "search"),
                Str(root, "web", "scrape"));
        }
        catch (JsonException)
        {
            return Empty with { Corrupt = true };
        }
    }

    // Same "local" set the bash launcher uses to decide whether it manages
    // inference itself: loopback or the container host route.
    public static bool IsLocalEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && (uri.IsLoopback || string.Equals(uri.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase));

    private static string? Str(JsonElement root, string section, string key) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(section, out var s) && s.ValueKind == JsonValueKind.Object
        && s.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()
            : null;

    private static bool? Bool(JsonElement root, string section, string key)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(section, out var s) || s.ValueKind != JsonValueKind.Object
            || !s.TryGetProperty(key, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
