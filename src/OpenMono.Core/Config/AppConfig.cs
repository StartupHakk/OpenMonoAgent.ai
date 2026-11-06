using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenMono.Acp;
using OpenMono.Utils;

namespace OpenMono.Config;

public sealed class AppConfig
{
    public LlmConfig Llm { get; set; } = new();
    public WebConfig Web { get; set; } = new();
    public PermissionConfig Permissions { get; set; } = new();
    public SecretWritePolicy SecretWrites { get; set; } = SecretWritePolicy.Block;
    public HookConfig Hooks { get; set; } = new();
    public PlaybookConfig Playbooks { get; set; } = new();
    public AgentConfig Agents { get; set; } = new();
    public Dictionary<string, ProviderSettings> Providers { get; set; } = [];
    public Dictionary<string, ModelPresetSettings> ModelPresets { get; set; } = [];
    public Dictionary<string, McpServerSettings> McpServers { get; set; } = [];
    public AcpServerSettings? AcpServer { get; set; }
    public InferenceConfig Inference { get; set; } = new();
    public bool AutoDetectCodeGraph { get; set; } = true;
    public bool Verbose { get; set; } = false;
    public bool ShowDetail { get; set; } = false;
    [JsonIgnore] // derived by ResolveVision(); the real key is inference.vision_enabled
    public bool VisionEnabled { get; set; }
    // Resolution order: settings.json inference.vision_enabled (explicit) →
    // OPENMONO_VISION_ENABLED env (set by the container launcher) → false.
    public void ResolveVision()
        => VisionEnabled = Inference.VisionEnabled
            ?? Environment.GetEnvironmentVariable("OPENMONO_VISION_ENABLED") == "1";
    // Parse the "modalities.vision" capability llama.cpp reports in /props.
    // Returns null when the server doesn't advertise it (unknown — don't guess).
    public static bool? ParsePropsVision(JsonElement root)
    {
        if (root.TryGetProperty("modalities", out var modalities)
            && modalities.ValueKind == JsonValueKind.Object
            && modalities.TryGetProperty("vision", out var visionEl))
        {
            if (visionEl.ValueKind == JsonValueKind.True) return true;
            if (visionEl.ValueKind == JsonValueKind.False) return false;
        }
        return null;
    }
    // Use OPENMONO_WORKSPACE if running in Docker container, otherwise use current directory
    public string WorkingDirectory { get; set; } = GetWorkingDirectory();

    private static string GetWorkingDirectory()
    {
        var envVar = Environment.GetEnvironmentVariable("OPENMONO_WORKSPACE");
        var cwd = Directory.GetCurrentDirectory();
        return envVar ?? cwd;
    }
    public string? HostWorkingDirectory { get; set; }
    public string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openmono");
}

public sealed class ProviderSettings
{
    public string? ApiKey { get; set; }
    public string? Endpoint { get; set; }
    public string? Model { get; set; }
    public bool Active { get; set; }
}

public sealed class McpServerSettings
{
    public required string Command { get; set; }
    public string[]? Args { get; set; }
    public Dictionary<string, string>? Env { get; set; }
    public string? WorkingDirectory { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class ModelPresetSettings : LlmConfig
{
    public bool Active { get; set; }
}

public class LlmConfig
{
    public string Endpoint { get; set; } = "http://localhost:7474";
    public string Model { get; set; } = "";
    public string? ApiKey { get; set; }
    public int ContextSize { get; set; } = 196608;
    public int MaxOutputTokens { get; set; } = 16384;
    public int MaxConcurrentRequests { get; set; } = 2;
    public double Temperature { get; set; } = 0.7;
    public double TopP { get; set; } = 0.8;
    public int TopK { get; set; } = 20;
    public double PresencePenalty { get; set; } = 1.5;
    public double MinP { get; set; } = 0.0;
    public double RepetitionPenalty { get; set; } = 1.0;

    [JsonIgnore]
    public bool ContextSizeExplicit { get; set; }

    [JsonIgnore]
    public ServerReasoningInfo? ServerReasoning { get; set; }

    public void MergeFrom(LlmConfig source)
    {
        if (!string.IsNullOrEmpty(source.Endpoint)) Endpoint = source.Endpoint;
        if (!string.IsNullOrEmpty(source.Model)) Model = source.Model;
        if (!string.IsNullOrEmpty(source.ApiKey)) ApiKey = source.ApiKey;
        if (source.ContextSize > 0) ContextSize = source.ContextSize;
        if (source.MaxOutputTokens > 0) MaxOutputTokens = source.MaxOutputTokens;
        if (source.Temperature > 0) Temperature = source.Temperature;
        if (source.TopP > 0) TopP = source.TopP;
        if (source.TopK > 0) TopK = source.TopK;
        if (source.PresencePenalty != 0) PresencePenalty = source.PresencePenalty;
        if (source.MinP > 0) MinP = source.MinP;
        if (source.RepetitionPenalty > 0) RepetitionPenalty = source.RepetitionPenalty;
    }
}

public sealed class WebConfig
{
    public string? Gateway { get; set; }

    // The launcher (openmono config set web.search true) writes JSON booleans,
    // but this model is string-based (Truthy()). Read either form so a boolean
    // never throws and skips the whole settings file.
    [JsonConverter(typeof(FlexibleBoolToStringConverter))]
    public string? Search { get; set; }

    [JsonConverter(typeof(FlexibleBoolToStringConverter))]
    public string? Scrape { get; set; }

    public bool? SearchEnabled => Truthy(Search);
    public bool? ScrapeEnabled => Truthy(Scrape);

    public void MergeFrom(WebConfig source)
    {
        if (!string.IsNullOrEmpty(source.Gateway)) Gateway = source.Gateway;
        if (!string.IsNullOrEmpty(source.Search)) Search = source.Search;
        if (!string.IsNullOrEmpty(source.Scrape)) Scrape = source.Scrape;
    }

    private static bool? Truthy(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
}

// Accepts a JSON string, boolean, or number and stores it as a string, so a
// boolean written by 'openmono config set web.search true' deserializes cleanly
// instead of throwing (which would skip the entire settings file).
public sealed class FlexibleBoolToStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.True  => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString(CultureInfo.InvariantCulture) : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null   => null,
            _ => throw new JsonException($"Expected string, boolean, or number for web setting (got {reader.TokenType})."),
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}

public sealed class PermissionConfig
{
    public Dictionary<string, ToolPermissionRules> Tools { get; set; } = [];
}

public sealed class ToolPermissionRules
{
    public List<string> Allow { get; set; } = [];
    public List<string> Deny { get; set; } = [];
    public List<string> Ask { get; set; } = [];
}

public sealed class HookConfig
{
    public List<HookDefinition> PreToolUse { get; set; } = [];
    public List<HookDefinition> PostToolUse { get; set; } = [];
    public List<HookDefinition> SessionStart { get; set; } = [];
}

public sealed class HookDefinition
{
    [JsonPropertyName("if")]
    public HookCondition? Condition { get; set; }
    public required string Run { get; set; }
}

public sealed class HookCondition
{
    public string? Tool { get; set; }
    public string? InputContains { get; set; }
}

public sealed class PlaybookConfig
{
    public List<string> Paths { get; set; } = [".openmono/playbooks/", "~/.openmono/playbooks/"];
}

public sealed class AgentConfig
{
    public int MaxConcurrentAgents { get; set; } = 2;
    public int MaxNestingDepth { get; set; } = 3;
    public int MaxQueuedAgents { get; set; } = 4;
    public int MaxConcurrentPerParent { get; set; } = 2;

    public void MergeFrom(AgentConfig source)
    {
        if (source.MaxConcurrentAgents > 0) MaxConcurrentAgents = source.MaxConcurrentAgents;
        if (source.MaxNestingDepth > 0) MaxNestingDepth = source.MaxNestingDepth;
        if (source.MaxQueuedAgents > 0) MaxQueuedAgents = source.MaxQueuedAgents;
        if (source.MaxConcurrentPerParent > 0) MaxConcurrentPerParent = source.MaxConcurrentPerParent;
    }
}

/// <summary>
/// Local inference-server settings (the "inference" section of settings.json,
/// written by the installer). Currently only CtxSize feeds the agent: it
/// becomes Llm.ContextSize unless OPENMONO_CONTEXT_SIZE overrides it.
/// </summary>
public sealed class InferenceConfig
{
    public int CtxSize { get; set; }

    // Explicit vision/image-input choice (settings.json: inference.vision_enabled).
    // Null = not set here: the OPENMONO_VISION_ENABLED env var wins (container
    // launches pass it through). Set = authoritative across every launcher.
    public bool? VisionEnabled { get; set; }

    public void MergeFrom(InferenceConfig source)
    {
        if (source.CtxSize > 0) CtxSize = source.CtxSize;
        if (source.VisionEnabled.HasValue) VisionEnabled = source.VisionEnabled;
    }
}
