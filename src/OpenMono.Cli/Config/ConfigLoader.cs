using System.Text.Json;

namespace OpenMono.Config;

public static class ConfigLoader
{
    public static AppConfig Load(
        string? workingDirectory = null,
        string? configPath = null,
        Action<string>? warn = null)
    {
        var config = new AppConfig();
        var cwd = workingDirectory ?? Directory.GetCurrentDirectory();
        config.WorkingDirectory = cwd;

        config.ModelPresets["qwen"] = new ModelPresetSettings
        {
            Temperature = 0.7,
            TopP = 0.8,
            TopK = 20,
            PresencePenalty = 1.5,
            MinP = 0.0,
            RepetitionPenalty = 1.0,
        };

        var userConfigPath = Path.Combine(config.DataDirectory, "settings.json");
        MergeFromFile(config, userConfigPath, warn);

        var projectConfigPath = Path.Combine(cwd, ".openmono", "settings.json");
        MergeFromFile(config, projectConfigPath, warn);

        if (configPath is not null)
            MergeFromFile(config, configPath, warn);

        ApplyEnvironmentOverrides(config);

        ApplyActiveModelPreset(config);

        try
        {
            Directory.CreateDirectory(config.DataDirectory);
            Directory.CreateDirectory(Path.Combine(config.DataDirectory, "sessions"));
            Directory.CreateDirectory(Path.Combine(config.DataDirectory, "memory"));
        }
        catch (UnauthorizedAccessException ex)
        {
            warn?.Invoke($"Cannot create data directory {config.DataDirectory}: {ex.Message}");
        }

        return config;
    }

    private static void MergeFromFile(AppConfig config, string path, Action<string>? warn)
    {
        if (!File.Exists(path)) return;

        try
        {
            var json = File.ReadAllText(path);
            var overrides = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions.Default);
            if (overrides is null) return;

            config.Llm.MergeFrom(overrides.Llm);

            foreach (var (tool, rules) in overrides.Permissions.Tools)
            {
                if (!config.Permissions.Tools.TryGetValue(tool, out var existing))
                {
                    config.Permissions.Tools[tool] = rules;
                }
                else
                {
                    existing.Allow.AddRange(rules.Allow);
                    existing.Deny.AddRange(rules.Deny);
                    existing.Ask.AddRange(rules.Ask);
                }
            }

            config.Hooks.PreToolUse.AddRange(overrides.Hooks.PreToolUse);
            config.Hooks.PostToolUse.AddRange(overrides.Hooks.PostToolUse);
            config.Hooks.SessionStart.AddRange(overrides.Hooks.SessionStart);

            foreach (var (name, settings) in overrides.Providers)
                config.Providers[name] = settings;

            foreach (var (name, settings) in overrides.McpServers)
                config.McpServers[name] = settings;

            foreach (var (name, preset) in overrides.ModelPresets)
                config.ModelPresets[name] = preset;

            if (overrides.AutoDetectCodeGraph)
                config.AutoDetectCodeGraph = true;
        }
        catch (JsonException ex)
        {
            warn?.Invoke($"Malformed config file {path}: {ex.Message} — skipping");
        }
        catch (IOException ex)
        {
            warn?.Invoke($"Cannot read config file {path}: {ex.Message}");
        }
    }

    private static void ApplyActiveModelPreset(AppConfig config)
    {
        var active = config.ModelPresets.FirstOrDefault(p => p.Value.Active);
        if (active.Value is null) return;
        config.Llm.MergeFrom(active.Value);
    }

    private static void ApplyEnvironmentOverrides(AppConfig config)
    {
        var endpoint = Environment.GetEnvironmentVariable("OPENMONO_ENDPOINT");
        if (!string.IsNullOrEmpty(endpoint))
            config.Llm.Endpoint = endpoint;

        var model = Environment.GetEnvironmentVariable("OPENMONO_MODEL");
        if (!string.IsNullOrEmpty(model))
            config.Llm.Model = model;

        var apiKey = Environment.GetEnvironmentVariable("OPENMONO_API_KEY");
        if (!string.IsNullOrEmpty(apiKey))
            config.Llm.ApiKey = apiKey;

        var workspace = Environment.GetEnvironmentVariable("OPENMONO_WORKSPACE");
        if (!string.IsNullOrEmpty(workspace))
            config.WorkingDirectory = workspace;

        var hostWorkspace = Environment.GetEnvironmentVariable("OPENMONO_HOST_WORKSPACE");
        if (!string.IsNullOrEmpty(hostWorkspace))
            config.HostWorkingDirectory = hostWorkspace;

        var dataDir = Environment.GetEnvironmentVariable("OPENMONO_DATA_DIR");
        if (!string.IsNullOrEmpty(dataDir))
            config.DataDirectory = dataDir;

        var contextSize = Environment.GetEnvironmentVariable("OPENMONO_CONTEXT_SIZE");
        if (!string.IsNullOrEmpty(contextSize) && int.TryParse(contextSize, out var ctxVal) && ctxVal > 0)
            config.Llm.ContextSize = ctxVal;

        var maxOutput = Environment.GetEnvironmentVariable("OPENMONO_MAX_OUTPUT_TOKENS");
        if (!string.IsNullOrEmpty(maxOutput) && int.TryParse(maxOutput, out var maxOutVal) && maxOutVal > 0)
            config.Llm.MaxOutputTokens = maxOutVal;

        var topP = Environment.GetEnvironmentVariable("OPENMONO_TOP_P");
        if (!string.IsNullOrEmpty(topP) && double.TryParse(topP, out var topPVal) && topPVal > 0)
            config.Llm.TopP = topPVal;

        var topK = Environment.GetEnvironmentVariable("OPENMONO_TOP_K");
        if (!string.IsNullOrEmpty(topK) && int.TryParse(topK, out var topKVal) && topKVal > 0)
            config.Llm.TopK = topKVal;

        var presencePenalty = Environment.GetEnvironmentVariable("OPENMONO_PRESENCE_PENALTY");
        if (!string.IsNullOrEmpty(presencePenalty) && double.TryParse(presencePenalty, out var ppVal))
            config.Llm.PresencePenalty = ppVal;

        var minP = Environment.GetEnvironmentVariable("OPENMONO_MIN_P");
        if (!string.IsNullOrEmpty(minP) && double.TryParse(minP, out var minPVal) && minPVal > 0)
            config.Llm.MinP = minPVal;

        var repetitionPenalty = Environment.GetEnvironmentVariable("OPENMONO_REPETITION_PENALTY");
        if (!string.IsNullOrEmpty(repetitionPenalty) && double.TryParse(repetitionPenalty, out var rpVal) && rpVal > 0)
            config.Llm.RepetitionPenalty = rpVal;

        var modelPreset = Environment.GetEnvironmentVariable("OPENMONO_MODEL_PRESET");
        if (!string.IsNullOrEmpty(modelPreset) && config.ModelPresets.TryGetValue(modelPreset, out var mp))
        {
            foreach (var p in config.ModelPresets.Values) p.Active = false;
            mp.Active = true;
        }

        var provider = Environment.GetEnvironmentVariable("OPENMONO_PROVIDER");
        if (!string.IsNullOrEmpty(provider) && config.Providers.TryGetValue(provider, out var ps))
        {

            foreach (var p in config.Providers.Values) p.Active = false;
            ps.Active = true;
        }
    }
}
