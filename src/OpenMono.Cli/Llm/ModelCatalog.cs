using System.Net.Http.Headers;
using System.Text.Json;
using OpenMono.Utils;

namespace OpenMono.Llm;

/// <summary>
/// Process-wide cache of the models the active backend advertises as available for use.
/// Populated at startup from the OpenAI-compatible <c>/v1/models</c> endpoint (falling back to
/// the static provider catalog) and consumed by the <c>/model</c> command and the inline
/// suggestion overlay so users can see and pick a model at runtime.
/// </summary>
public static class ModelCatalog
{
    private static readonly object _lock = new();
    private static List<string> _models = [];

    /// <summary>Snapshot of the currently known models, in advertised order.</summary>
    public static IReadOnlyList<string> Models
    {
        get { lock (_lock) return _models.ToList(); }
    }

    public static void Set(IEnumerable<string> models)
    {
        lock (_lock)
        {
            _models = models
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public static bool Contains(string model)
    {
        lock (_lock)
            return _models.Any(m => string.Equals(m, model, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when the active endpoint advertises more than one model. A fallback-seeded
    /// single model counts as single, so the <c>/model</c> picker only makes sense here.
    /// </summary>
    public static bool IsMultiModel
    {
        get { lock (_lock) return _models.Count > 1; }
    }

    /// <summary>
    /// Query the backend's <c>/v1/models</c> endpoint and return every advertised model id.
    /// Returns an empty list if the endpoint is unavailable or reports no models.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FetchAsync(
        string endpoint, string? apiKey, CancellationToken ct = default)
    {
        var models = new List<string>();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            if (!string.IsNullOrWhiteSpace(apiKey))
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var url = $"{endpoint.TrimEnd('/')}/v1/models";
            var json = await http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var idEl)
                        && idEl.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(idEl.GetString()))
                    {
                        models.Add(idEl.GetString()!);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"ModelCatalog: /v1/models unavailable ({ex.GetType().Name})");
        }

        return models;
    }

    /// <summary>
    /// Refresh the cache from the backend. If the backend reports no models, keep any that are
    /// already cached and, as a last resort, seed with <paramref name="fallback"/>.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RefreshAsync(
        string endpoint, string? apiKey, IEnumerable<string>? fallback = null, CancellationToken ct = default)
    {
        var fetched = await FetchAsync(endpoint, apiKey, ct);
        if (fetched.Count > 0)
        {
            Set(fetched);
        }
        else if (Models.Count == 0 && fallback is not null)
        {
            Set(fallback);
        }
        return Models;
    }

    /// <summary>
    /// Outcome of a reachability probe against a single backend's <c>/v1/models</c> endpoint.
    /// </summary>
    public sealed record BackendProbe(bool Ok, IReadOnlyList<string> Models)
    {
        /// <summary>True when the backend advertises more than one model (a "multi-model" server).</summary>
        public bool IsMulti => Models.Count > 1;
    }

    /// <summary>
    /// Probes a set of backends in parallel (each with a short timeout) and returns their
    /// reachability + advertised model ids. Used by <c>/models</c> to mark unreachable
    /// backends as offline in the menu.
    /// </summary>
    public static async Task<Dictionary<string, BackendProbe>> ProbeAsync(
        IEnumerable<(string Name, string Endpoint, string? Key)> backends, CancellationToken ct)
    {
        var list = backends.ToList();
        var tasks = list.Select(async b =>
        {
            var models = await FetchAsync(b.Endpoint, b.Key, ct);
            // Many working backends (Anthropic-style endpoints, some llama-server
            // configs, gateways) do not serve /v1/models. Treat a reachable TCP
            // endpoint as online so the menu doesn't show a false OFFLINE.
            var reachable = models.Count > 0 || await TcpReachableAsync(b.Endpoint, ct);
            return (Name: b.Name, Probe: new BackendProbe(reachable, models));
        }).ToList();
        var done = await Task.WhenAll(tasks);
        return done.ToDictionary(r => r.Name, r => r.Probe, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Live per-slot context windows reported by a llama.cpp-style backend's <c>/slots</c>
    /// endpoint. Fetched fresh on every call — deliberately never cached — so the Models tab
    /// shows the REAL context size one slot allows, and updates immediately if the server is
    /// restarted with a different size. Returns an empty list when the backend does not serve
    /// <c>/slots</c> (non-llama backends) or the response can't be parsed, so callers can show
    /// "unknown" rather than a guessed number.
    /// </summary>
    public static async Task<IReadOnlyList<(string? Model, int NCtx)>> ProbeContextWindowsAsync(
        string endpoint, string? apiKey, CancellationToken ct)
    {
        var results = new List<(string? Model, int NCtx)>();
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            if (!string.IsNullOrWhiteSpace(apiKey))
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            var baseUrl = endpoint.TrimEnd('/');
            var json = await http.GetStringAsync($"{baseUrl}/slots", ct);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                return results;
            foreach (var slot in doc.RootElement.EnumerateArray())
            {
                if (slot.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                if (!slot.TryGetProperty("n_ctx", out var nCtx)
                    || !nCtx.TryGetInt32(out var ctx)
                    || ctx <= 0)
                    continue;
                string? model = null;
                foreach (var field in new[] { "model", "model_path", "model_alias" })
                {
                    if (slot.TryGetProperty(field, out var mEl)
                        && mEl.ValueKind == System.Text.Json.JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(mEl.GetString()))
                    {
                        model = mEl.GetString();
                        break;
                    }
                }
                results.Add((model, ctx));
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"ModelCatalog: /slots unavailable ({ex.GetType().Name})");
        }
        return results;
    }

    /// <summary>
    /// Raw TCP connect to the endpoint's host:port (HTTPS-aware default port).
    /// Used as a reachability fallback when /v1/models is absent.
    /// </summary>
    private static async Task<bool> TcpReachableAsync(string endpoint, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return false;
            var port = uri.Port > 0 ? uri.Port : uri.Scheme == Uri.UriSchemeHttps ? 443 : 80;
            using var client = new System.Net.Sockets.TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(uri.Host, port, timeoutCts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
