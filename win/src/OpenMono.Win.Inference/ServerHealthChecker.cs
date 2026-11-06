using System.Net;
using System.Text.Json;

namespace OpenMono.Win.Inference;

// What a local OpenAI-compatible endpoint currently offers. Never throws:
// an unreachable server is a normal state (nothing serving yet), not an error.
// llama-server answers /health with 503 while the model loads and 200 once it
// serves, so Ready is the readiness bit the launcher waits on.
public sealed record ServerHealth(
    bool Reachable,
    bool Ready,
    bool Loading,
    IReadOnlyList<string> Models,
    string? Error)
{
    public bool ListsModel(string alias) =>
        Models.Any(m => string.Equals(StripGguf(m), StripGguf(alias), StringComparison.OrdinalIgnoreCase));

    private static string StripGguf(string name) =>
        name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
}

public sealed class ServerHealthChecker
{
    private readonly HttpClient _http;

    public ServerHealthChecker(HttpClient http)
    {
        _http = http;
    }

    public async Task<ServerHealth> CheckAsync(string endpointRoot, CancellationToken ct = default)
    {
        var root = OmaEndpointContract.TrimRoot(endpointRoot);
        bool? healthOk = null;
        var loading = false;
        string? error = null;

        try
        {
            using var response = await _http.GetAsync(root + OmaEndpointContract.HealthPath, ct);
            healthOk = response.IsSuccessStatusCode;
            loading = response.StatusCode == HttpStatusCode.ServiceUnavailable;
            if (!response.IsSuccessStatusCode && !loading)
            {
                error = $"/health returned {(int)response.StatusCode}.";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            error = $"unreachable: {ex.GetType().Name}";
        }

        var models = new List<string>();
        var modelsOk = false;
        if (healthOk is not null)
        {
            try
            {
                using var response = await _http.GetAsync(root + OmaEndpointContract.ModelsPath, ct);
                if (response.IsSuccessStatusCode)
                {
                    models.AddRange(ParseModelIds(await response.Content.ReadAsStringAsync(ct)));
                    modelsOk = true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                // /health answered; a failed model list leaves Models empty.
            }
        }

        var reachable = healthOk is not null;
        // A remote OpenAI-compatible server may have no /health at all; a
        // model list it serves counts as ready unless it reported loading.
        var ready = healthOk == true || (modelsOk && !loading);
        return new ServerHealth(reachable, ready, loading && !ready, models, ready ? null : error ?? (loading ? "loading the model" : null));
    }

    public static IReadOnlyList<string> ParseModelIds(string json)
    {
        var ids = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id)
                        && id.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(id.GetString()))
                    {
                        ids.Add(id.GetString()!);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Malformed body means "no usable model list", not a crash.
        }

        return ids;
    }
}
