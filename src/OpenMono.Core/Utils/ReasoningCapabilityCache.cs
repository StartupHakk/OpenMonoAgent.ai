using System.Net.Http.Headers;
using System.Text.Json;

namespace OpenMono.Utils;

/// <summary>
/// Caches what inference servers actually advertise about reasoning
/// (<see cref="ServerReasoningInfo"/>) keyed by <c>(endpoint, model)</c>. The
/// startup probe (<c>TryDetectActualModelAsync</c>) feeds it; <c>/model</c>
/// switches and session loads consult it so the chat-template probe runs once
/// per (endpoint, model) instead of per process, and a persisted thinking level
/// can be validated against what the server really advertises.
/// </summary>
/// <remarks>
/// Two levels: process memory + a small JSON file under the data directory, so
/// the TUI and the long-lived ACP server share one probe result. Writes are
/// best-effort — a corrupt or missing file simply means "probe again".
/// </remarks>
public sealed class ReasoningCapabilityCache
{
    private readonly string? _filePath;
    private readonly Dictionary<string, ServerReasoningInfo> _entries;
    private readonly object _gate = new();

    public ReasoningCapabilityCache(string? dataDirectory = null)
    {
        _filePath = dataDirectory is null
            ? null
            : Path.Combine(dataDirectory, "reasoning-capabilities.json");
        _entries = _filePath is not null && File.Exists(_filePath) ? Load(_filePath) : new();
    }

    private static string Key(string endpoint, string? model)
        => endpoint.TrimEnd('/') + "|" + (model ?? "").ToLowerInvariant();

    /// <summary>Cached probe result for this (endpoint, model), or null.</summary>
    public ServerReasoningInfo? Get(string endpoint, string? model)
    {
        lock (_gate)
            return _entries.TryGetValue(Key(endpoint, model), out var info) ? info : null;
    }

    /// <summary>Store a probe result (called by the startup probe and after /model re-probes).</summary>
    public void Set(string endpoint, string? model, ServerReasoningInfo info)
    {
        lock (_gate)
        {
            _entries[Key(endpoint, model)] = info;
            Persist();
        }
    }

    public void Invalidate(string endpoint, string? model)
    {
        lock (_gate)
        {
            if (_entries.Remove(Key(endpoint, model)))
                Persist();
        }
    }

    /// <summary>
    /// Fetch <c>{endpoint}/props</c> and parse the advertised reasoning
    /// capabilities. Returns null when the server is unreachable or the reply
    /// can't be parsed — callers then fall back to name heuristics
    /// (<see cref="ModelReasoningProfile.Resolve(string)"/>).
    /// </summary>
    public static async Task<ServerReasoningInfo?> ProbeAsync(string endpoint, string? apiKey, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            if (!string.IsNullOrWhiteSpace(apiKey))
                http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", apiKey);
            var json = await http.GetStringAsync(endpoint.TrimEnd('/') + "/props", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? reasoningFormat = null;
            var reasoningInContent = false;
            string? chatTemplate = null;
            if (root.TryGetProperty("default_generation_settings", out var rdgs)
                && rdgs.TryGetProperty("params", out var rparams)
                && rparams.ValueKind == JsonValueKind.Object)
            {
                if (rparams.TryGetProperty("reasoning_format", out var rf)
                    && rf.ValueKind == JsonValueKind.String)
                    reasoningFormat = rf.GetString();
                if (rparams.TryGetProperty("reasoning_in_content", out var ric)
                    && ric.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    reasoningInContent = ric.GetBoolean();
            }
            if (root.TryGetProperty("chat_template", out var ctEl)
                && ctEl.ValueKind == JsonValueKind.String)
                chatTemplate = ctEl.GetString();

            var (effortLevels, effortDefault) = ServerReasoningInfo.ParseEffortLevels(chatTemplate);

            int? contextWindow = null;
            if (root.TryGetProperty("default_generation_settings", out var cgs)
                && cgs.ValueKind == JsonValueKind.Object
                && cgs.TryGetProperty("n_ctx", out var nCtxNested)
                && nCtxNested.TryGetInt32(out var ctxNested) && ctxNested > 0)
            {
                contextWindow = ctxNested;
            }
            else if (root.TryGetProperty("n_ctx", out var nCtx)
                && nCtx.TryGetInt32(out var ctx) && ctx > 0)
            {
                contextWindow = ctx;
            }

            return new ServerReasoningInfo
            {
                ReasoningFormat = reasoningFormat,
                ReasoningInContent = reasoningInContent,
                HasThinkingTemplate = ServerReasoningInfo.TemplateShowsThinking(chatTemplate),
                EffortLevels = effortLevels,
                EffortDefaultLevel = effortDefault,
                ContextWindow = contextWindow,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Debug($"Reasoning capability probe failed for {endpoint}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Get the cached capabilities for (endpoint, model), probing and caching on
    /// miss. Null when the server doesn't answer /props.
    /// </summary>
    public async Task<ServerReasoningInfo?> GetOrProbeAsync(string endpoint, string? apiKey, string? model, CancellationToken ct)
    {
        var cached = Get(endpoint, model);
        if (cached is not null) return cached;
        var probed = await ProbeAsync(endpoint, apiKey, ct);
        if (probed is not null)
            Set(endpoint, model, probed);
        return probed;
    }

    private void Persist()
    {
        if (_filePath is null) return;
        try
        {
            var tmp = $"{_filePath}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmp,
                JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Debug($"Reasoning capability cache persist failed: {ex.Message}");
        }
    }

    private static Dictionary<string, ServerReasoningInfo> Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, ServerReasoningInfo>>(
                File.ReadAllText(path)) ?? new();
        }
        catch
        {
            return new();
        }
    }
}
