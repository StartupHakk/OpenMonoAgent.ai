using System.Text.Json;

namespace OpenMono.Win.Foundry;

// What a local OpenAI-compatible endpoint currently offers. Never throws:
// an unreachable server is a normal state (nothing serving yet), not an error.
public sealed record FoundryHealth(
    bool Reachable,
    IReadOnlyList<string> Models,
    bool HealthEndpointOk,
    string? Error)
{
    public bool ListsModel(string alias) =>
        FoundryModelIdResolver.ResolveServedId(alias, Models) is not null;
}

public sealed class FoundryHealthChecker
{
    private readonly HttpClient _http;

    public FoundryHealthChecker(HttpClient http)
    {
        _http = http;
    }

    public async Task<FoundryHealth> CheckAsync(string endpointRoot, CancellationToken ct = default)
    {
        var root = endpointRoot.TrimEnd('/');
        var models = new List<string>();
        var modelsOk = false;
        string? error = null;

        try
        {
            using var response = await _http.GetAsync(root + "/v1/models", ct);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                models.AddRange(ParseModelIds(json));
                modelsOk = true;
            }
            else
            {
                error = $"/v1/models returned {(int)response.StatusCode}.";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            error = $"unreachable: {ex.GetType().Name}";
        }
        catch (Exception ex)
        {
            error = $"unexpected: {ex.Message}";
        }

        var healthOk = false;
        try
        {
            using var response = await _http.GetAsync(root + "/health", ct);
            healthOk = response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            // /health is optional (llama.cpp has it, Foundry web service may
            // not). Its absence never fails the check on its own.
        }

        // A daemon that answers /v1/models with an empty list is up but has
        // nothing loaded yet; that is reachable, and the caller loads a model.
        var reachable = modelsOk || healthOk;
        if (!reachable && error is null)
        {
            error = "no /v1/models or /health response.";
        }

        return new FoundryHealth(reachable, models, healthOk, reachable ? null : error);
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
