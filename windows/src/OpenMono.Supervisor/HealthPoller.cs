namespace OpenMono.Windows.Supervisor;

/// <summary>
/// Health gating for llama-server and the Docker gateway. Polls /health with
/// the same 180 second patience as scripts/install.sh and macOS inference.sh.
/// </summary>
public sealed class HealthPoller
{
    private readonly HttpClient _http;

    public HealthPoller(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    public sealed record HealthResult(bool Healthy, string? Model, int Attempts, TimeSpan Elapsed);

    public async Task<bool> IsHealthyAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync($"{baseUrl.TrimEnd('/')}/health", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<HealthResult> WaitForHealthyAsync(
        string baseUrl,
        TimeSpan? timeout = null,
        TimeSpan? interval = null,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        timeout ??= TimeSpan.FromSeconds(180);
        interval ??= TimeSpan.FromSeconds(5);
        var started = DateTime.UtcNow;
        int attempts = 0;
        while (DateTime.UtcNow - started < timeout)
        {
            ct.ThrowIfCancellationRequested();
            attempts++;
            progress?.Report(attempts);
            if (await IsHealthyAsync(baseUrl, ct))
            {
                string? model = await TryDetectModelAsync(baseUrl, ct);
                return new HealthResult(true, model, attempts, DateTime.UtcNow - started);
            }

            await Task.Delay(interval.Value, ct);
        }

        return new HealthResult(false, null, attempts, DateTime.UtcNow - started);
    }

    /// <summary>
    /// Model detection order matches Program.cs TryDetectActualModelAsync:
    /// /props first, then /v1/models.
    /// </summary>
    public async Task<string?> TryDetectModelAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using var props = await _http.GetAsync($"{baseUrl.TrimEnd('/')}/props", ct);
            if (props.IsSuccessStatusCode)
            {
                var text = await props.Content.ReadAsStringAsync(ct);
                var name = ParsePropsModel(text);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }
        catch
        {
        }

        try
        {
            using var models = await _http.GetAsync($"{baseUrl.TrimEnd('/')}/v1/models", ct);
            if (models.IsSuccessStatusCode)
            {
                var text = await models.Content.ReadAsStringAsync(ct);
                return ParseModelsModel(text);
            }
        }
        catch
        {
        }

        return null;
    }

    public static string? ParsePropsModel(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("default_generation_settings", out var settings)
                && settings.TryGetProperty("model", out var model))
            {
                return model.GetString();
            }

            if (doc.RootElement.TryGetProperty("model", out var direct))
            {
                return direct.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    public static string? ParseModelsModel(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == System.Text.Json.JsonValueKind.Array
                && data.GetArrayLength() > 0
                && data[0].TryGetProperty("id", out var id))
            {
                return id.GetString();
            }
        }
        catch
        {
        }

        return null;
    }
}
