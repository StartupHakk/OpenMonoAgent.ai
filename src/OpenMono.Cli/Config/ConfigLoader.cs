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
        var cwd = Path.GetFullPath(workingDirectory ?? Directory.GetCurrentDirectory());
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

        EnsureWritableDataDirectory(config, warn);

        return config;
    }

    private static readonly string[] DataSubdirectories = ["sessions", "memory", "artifacts"];

    private static void EnsureWritableDataDirectory(AppConfig config, Action<string>? warn)
    {
        if (TryInitDataDirectory(config.DataDirectory))
            return;

        var fallback = Path.Combine(Path.GetTempPath(), "openmono");
        var fallbackIsConfigured = string.Equals(
            Path.GetFullPath(fallback), Path.GetFullPath(config.DataDirectory), StringComparison.Ordinal);

        if (fallbackIsConfigured || !TryInitDataDirectory(fallback))
        {
            warn?.Invoke(
                $"Data directory '{config.DataDirectory}' is not writable and no fallback could be created. " +
                "Sessions, memory, and artifacts will not be saved this run.");
            return;
        }

        warn?.Invoke(
            $"Data directory '{config.DataDirectory}' is not writable — falling back to '{fallback}'. " +
            "Sessions, memory, and artifacts will not persist across runs " +
            "(in Docker, mount a writable volume at the data dir or fix ~/.openmono ownership).");
        config.DataDirectory = fallback;
    }

    private static bool TryInitDataDirectory(string dataDirectory)
    {
        try
        {
            Directory.CreateDirectory(dataDirectory);
            ProbeWritable(dataDirectory);

            foreach (var sub in DataSubdirectories)
            {
                var path = Path.Combine(dataDirectory, sub);
                Directory.CreateDirectory(path);
                ProbeWritable(path);
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static void ProbeWritable(string directory)
    {
        var probe = Path.Combine(directory, $".writable-{Guid.NewGuid():N}");
        File.WriteAllText(probe, string.Empty);
        File.Delete(probe);
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
            config.Agents.MergeFrom(overrides.Agents);
            config.Web.MergeFrom(overrides.Web);

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

        var webGateway = Environment.GetEnvironmentVariable("OPENMONO_WEB_GATEWAY");
        if (!string.IsNullOrEmpty(webGateway))
            config.Web.Gateway = webGateway;

        var webSearch = Environment.GetEnvironmentVariable("OPENMONO_WEB_SEARCH");
        if (!string.IsNullOrEmpty(webSearch))
            config.Web.Search = webSearch;

        var webScrape = Environment.GetEnvironmentVariable("OPENMONO_WEB_SCRAPE");
        if (!string.IsNullOrEmpty(webScrape))
            config.Web.Scrape = webScrape;

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

        // Multi-model tiers: operator (fast tool-calling) and coder (code generation)
        var opEndpoint = Environment.GetEnvironmentVariable("OPENMONO_OPERATOR_ENDPOINT");
        var opModel = Environment.GetEnvironmentVariable("OPENMONO_OPERATOR_MODEL");
        if (!string.IsNullOrEmpty(opEndpoint) || !string.IsNullOrEmpty(opModel))
        {
            config.ModelTiers.Operator = new ModelTierConfig
            {
                Endpoint = opEndpoint ?? config.Llm.Endpoint,
                Model = opModel ?? "",
                ApiKey = Environment.GetEnvironmentVariable("OPENMONO_OPERATOR_API_KEY") ?? config.Llm.ApiKey,
                ContextSize = int.TryParse(Environment.GetEnvironmentVariable("OPENMONO_OPERATOR_CONTEXT_SIZE"), out var opCtx) ? opCtx : config.Llm.ContextSize,
                MaxOutputTokens = int.TryParse(Environment.GetEnvironmentVariable("OPENMONO_OPERATOR_MAX_OUTPUT"), out var opMax) ? opMax : 8192,
                Temperature = double.TryParse(Environment.GetEnvironmentVariable("OPENMONO_OPERATOR_TEMPERATURE"), out var opTemp) ? opTemp : 0.3,
                MaxConcurrentRequests = 2,
            };
        }

        var coderEndpoint = Environment.GetEnvironmentVariable("OPENMONO_CODER_ENDPOINT");
        var coderModel = Environment.GetEnvironmentVariable("OPENMONO_CODER_MODEL");
        if (!string.IsNullOrEmpty(coderEndpoint) || !string.IsNullOrEmpty(coderModel))
        {
            config.ModelTiers.Coder = new ModelTierConfig
            {
                Endpoint = coderEndpoint ?? config.Llm.Endpoint,
                Model = coderModel ?? "",
                ApiKey = Environment.GetEnvironmentVariable("OPENMONO_CODER_API_KEY") ?? config.Llm.ApiKey,
                ContextSize = int.TryParse(Environment.GetEnvironmentVariable("OPENMONO_CODER_CONTEXT_SIZE"), out var coderCtx) ? coderCtx : config.Llm.ContextSize,
                MaxOutputTokens = int.TryParse(Environment.GetEnvironmentVariable("OPENMONO_CODER_MAX_OUTPUT"), out var coderMax) ? coderMax : 8192,
                Temperature = double.TryParse(Environment.GetEnvironmentVariable("OPENMONO_CODER_TEMPERATURE"), out var coderTemp) ? coderTemp : 0.2,
                MaxConcurrentRequests = 2,
            };
        }
    }
}
