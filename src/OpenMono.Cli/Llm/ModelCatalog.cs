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
}
