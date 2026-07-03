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
    public bool AutoDetectCodeGraph { get; set; } = true;
    public bool Verbose { get; set; } = false;
    public bool ShowDetail { get; set; } = false;
    public bool VisionEnabled { get; set; } =
        Environment.GetEnvironmentVariable("OPENMONO_VISION_ENABLED") == "1";
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

    public void MergeFrom(LlmConfig source)
    {
        if (!string.IsNullOrEmpty(source.Endpoint)) Endpoint = source.Endpoint;
        if (!string.IsNullOrEmpty(source.Model)) Model = source.Model;
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
    public string? Search { get; set; }
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
