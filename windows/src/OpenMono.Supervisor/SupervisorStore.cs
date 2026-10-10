using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenMono.Windows.Supervisor;

/// <summary>
/// Persists user-managed supervisor fields to %LOCALAPPDATA%\OpenMono\app.json
/// under a "supervisor" section. Unknown root sections (e.g. future first-run
/// state) are preserved by merging. A missing or malformed file yields
/// defaults; malformed individual fields fall back to their defaults.
/// </summary>
public static class SupervisorStore
{
    public const string SectionName = "supervisor";
    public const string FileName = "app.json";

    public static string DefaultAppJsonPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenMono",
            FileName);

    public static SupervisorConfig Load(string? appJsonPath = null)
    {
        var config = new SupervisorConfig();
        JsonObject? section = ReadSection(appJsonPath ?? DefaultAppJsonPath());
        if (section is null)
        {
            return config;
        }

        return config with
        {
            LlamaPort = ReadPort(section, "llama_port", config.LlamaPort),
            AcpPort = ReadPort(section, "acp_port", config.AcpPort),
            GatewayPort = ReadPort(section, "gateway_port", config.GatewayPort),
            ModelsDirectory = ReadNonEmpty(section, "models_directory") ?? config.ModelsDirectory,
            VisionEnabled = ReadBool(section, "vision_enabled") ?? config.VisionEnabled,
            DockerServicesEnabled = ReadBool(section, "docker_services_enabled") ?? config.DockerServicesEnabled,
            AllowLanConnections = ReadBool(section, "allow_lan_connections") ?? config.AllowLanConnections,
            ApiKey = ReadNonEmpty(section, "api_key"),
            RemoteEndpointOverride = ReadNonEmpty(section, "remote_endpoint_override"),
        };
    }

    public static void Save(SupervisorConfig config, string? appJsonPath = null)
    {
        var path = appJsonPath ?? DefaultAppJsonPath();
        var root = LoadRoot(path);

        var section = new JsonObject
        {
            ["llama_port"] = config.LlamaPort,
            ["acp_port"] = config.AcpPort,
            ["gateway_port"] = config.GatewayPort,
            ["models_directory"] = config.ModelsDirectory,
            ["vision_enabled"] = config.VisionEnabled,
            ["docker_services_enabled"] = config.DockerServicesEnabled,
            ["allow_lan_connections"] = config.AllowLanConnections,
        };
        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            section["api_key"] = config.ApiKey;
        }

        if (!string.IsNullOrWhiteSpace(config.RemoteEndpointOverride))
        {
            section["remote_endpoint_override"] = config.RemoteEndpointOverride;
        }

        root[SectionName] = section;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static JsonObject LoadRoot(string path)
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
            // Malformed file: replaced with a fresh object below.
        }

        return new JsonObject();
    }

    private static JsonObject? ReadSection(string path)
    {
        var root = LoadRoot(path);
        try
        {
            return root[SectionName] as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static int ReadPort(JsonObject section, string key, int fallback)
    {
        try
        {
            if (section[key] is { } node)
            {
                var port = node.GetValue<int>();
                if (port is >= 1 and <= 65535)
                {
                    return port;
                }
            }
        }
        catch
        {
        }

        return fallback;
    }

    private static bool? ReadBool(JsonObject section, string key)
    {
        try
        {
            if (section[key] is { } node)
            {
                return node.GetValue<bool>();
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? ReadNonEmpty(JsonObject section, string key)
    {
        try
        {
            if (section[key] is { } node && node.GetValue<string>() is { } value && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        catch
        {
        }

        return null;
    }
}
