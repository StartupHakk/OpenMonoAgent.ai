using System.Text;
using System.Text.Json;

namespace OpenMono.Win.Foundry;

public enum FoundryCatalogDevice
{
    Cuda,
    Cpu,
    WebGpu,
}

// One catalog entry: the alias `foundry model load` takes plus the metadata
// the public catalog publishes for that variant.
public sealed record FoundryCatalogModel(
    string Alias,
    string? VariantId,
    string? Task,
    IReadOnlyList<string> InputModalities,
    bool? SupportsToolCalling,
    int? ContextLength,
    long? VramBytes,
    long? FileSizeBytes,
    string? Device,
    string? ExecutionProvider)
{
    public bool AcceptsImages => InputModalities.Contains("image", StringComparer.OrdinalIgnoreCase);
}

public sealed record WebCatalogResult(
    bool Ok,
    IReadOnlyList<FoundryCatalogModel> Models,
    string Source,
    string? Error,
    bool FromCache,
    DateTimeOffset? FetchedUtc);

// The public Foundry Local catalog behind https://foundrylocal.ai/models.
// That page renders in the browser from https://foundrylocal.ai/api/models,
// a POST that takes the same filter body the site sends and pages with
// continuationToken. This client never scrapes HTML. It lets setup and doctor
// validate aliases before (or without) a local Foundry install; once Foundry
// is installed, `foundry model list` stays the authority for what it can load.
public sealed class FoundryWebCatalogClient
{
    public const string ModelsPageUrl = "https://foundrylocal.ai/models";

    public const string DefaultApiUrl = "https://foundrylocal.ai/api/models";

    // Overrides the API URL (tests point it at a local fake); "off" disables it.
    public const string ApiUrlEnvVar = "OPENMONO_FOUNDRY_CATALOG_URL";

    public static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    private const int PageSize = 50;
    private const int MaxPages = 20;
    private const int MaxRedirects = 5;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions CacheJson = new() { WriteIndented = false };

    private readonly HttpClient _http;
    private readonly string _apiUrl;
    private readonly string? _cacheDir;
    private readonly Func<DateTimeOffset> _clock;

    public FoundryWebCatalogClient(HttpClient http, string? apiUrl = null, string? cacheDir = null, Func<DateTimeOffset>? clock = null)
    {
        _http = http;
        _apiUrl = string.IsNullOrWhiteSpace(apiUrl) ? DefaultApiUrl : apiUrl.Trim();
        _cacheDir = cacheDir;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public static FoundryWebCatalogClient FromEnvironment(HttpClient http, Func<string, string?> getEnv, string homeDir) =>
        new(http, getEnv(ApiUrlEnvVar), Path.Combine(homeDir, ".openmono", "win"));

    public string ApiUrl => _apiUrl;

    public bool Disabled => string.Equals(_apiUrl, "off", StringComparison.OrdinalIgnoreCase);

    public async Task<WebCatalogResult> ListAsync(
        FoundryCatalogDevice device,
        bool refresh = false,
        bool cacheOnly = false,
        CancellationToken ct = default)
    {
        if (Disabled)
        {
            return new WebCatalogResult(false, [], _apiUrl, $"disabled ({ApiUrlEnvVar}=off)", false, null);
        }

        var cached = ReadCache(device);
        if (cached is not null && !refresh && _clock() - cached.FetchedUtc < CacheTtl)
        {
            return cached;
        }

        if (cacheOnly)
        {
            return cached ?? new WebCatalogResult(false, [], _apiUrl, "no cached catalog", false, null);
        }

        try
        {
            var models = await FetchAllAsync(device, ct);
            var fetched = _clock();
            WriteCache(device, models, fetched);
            return new WebCatalogResult(true, models, _apiUrl, null, false, fetched);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or UriFormatException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            var error = ex is OperationCanceledException ? $"no answer within {RequestTimeout.TotalSeconds:F0} s" : ex.Message;
            return cached is not null
                ? cached with { Error = $"{error}; using the cached catalog" }
                : new WebCatalogResult(false, [], _apiUrl, error, false, null);
        }
    }

    // The filter body the catalog site sends for one device/execution provider.
    public static string BuildRequestBody(FoundryCatalogDevice device, string? continuationToken)
    {
        var (deviceName, provider) = device switch
        {
            FoundryCatalogDevice.Cpu => ("cpu", "CPUExecutionProvider"),
            FoundryCatalogDevice.WebGpu => ("gpu", "WebGpuExecutionProvider"),
            _ => ("gpu", "CUDAExecutionProvider"),
        };

        var filters = new object[]
        {
            Filter("type", "eq", "models"),
            Filter("kind", "eq", "Versioned"),
            Filter("labels", "eq", "latest"),
            Filter("annotations/systemCatalogData/deploymentOptions", "eq", "Foundry Local on Devices"),
            Filter("annotations/archived", "NotEquals", "true"),
            Filter("properties/variantInfo/variantMetadata/device", "eq", deviceName),
            Filter("properties/variantInfo/variantMetadata/executionProvider", "eq", provider),
        };

        return continuationToken is null
            ? JsonSerializer.Serialize(new { filters, pageSize = PageSize })
            : JsonSerializer.Serialize(new { filters, pageSize = PageSize, continuationToken });
    }

    public static (IReadOnlyList<FoundryCatalogModel> Models, string? ContinuationToken) ParsePage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("value", out var value)
            || value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("catalog answer has no \"value\" array");
        }

        var models = new List<FoundryCatalogModel>();
        foreach (var item in value.EnumerateArray())
        {
            var alias = Str(item, "annotations", "tags", "alias");
            if (string.IsNullOrWhiteSpace(alias))
            {
                continue;
            }

            var inputs = (Str(item, "annotations", "tags", "inputModalities") ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            models.Add(new FoundryCatalogModel(
                alias.Trim(),
                Str(item, "properties", "id"),
                Str(item, "annotations", "tags", "task"),
                inputs,
                Str(item, "annotations", "tags", "supportsToolCalling") is { } tools ? bool.TryParse(tools, out var t) ? t : null : null,
                int.TryParse(Str(item, "annotations", "tags", "contextLength"), out var ctx) ? ctx : null,
                Long(item, "properties", "variantInfo", "variantMetadata", "vRamFootprintBytes"),
                Long(item, "properties", "variantInfo", "variantMetadata", "fileSizeBytes"),
                Str(item, "properties", "variantInfo", "variantMetadata", "device"),
                Str(item, "properties", "variantInfo", "variantMetadata", "executionProvider")));
        }

        var token = root.TryGetProperty("continuationToken", out var tokenElement) && tokenElement.ValueKind == JsonValueKind.String
            ? tokenElement.GetString()
            : null;
        return (models, string.IsNullOrEmpty(token) ? null : token);
    }

    private static object Filter(string field, string op, string value) =>
        new { field, @operator = op, values = new[] { value } };

    private async Task<IReadOnlyList<FoundryCatalogModel>> FetchAllAsync(FoundryCatalogDevice device, CancellationToken ct)
    {
        var all = new List<FoundryCatalogModel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? token = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var (models, next) = ParsePage(await PostAsync(BuildRequestBody(device, token), ct));
            all.AddRange(models.Where(m => seen.Add(m.Alias)));
            if (next is null)
            {
                return all;
            }

            token = next;
        }

        throw new InvalidDataException($"catalog returned more than {MaxPages} pages");
    }

    // The catalog host redirects (307). HttpClient follows that on its own
    // with method and body intact; this loop covers clients that do not.
    private async Task<string> PostAsync(string body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);

        var url = new Uri(_apiUrl);
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendAsync(request, timeout.Token);

            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(url, location);
                if (url.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps)
                {
                    throw new HttpRequestException($"{_apiUrl} redirected away from https; refusing.");
                }

                url = next;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException($"{_apiUrl} answered HTTP {status}.");
            }

            return await response.Content.ReadAsStringAsync(timeout.Token);
        }

        throw new HttpRequestException($"{_apiUrl} redirected more than {MaxRedirects} times.");
    }

    private sealed record CacheFile(string ApiUrl, DateTimeOffset FetchedUtc, List<FoundryCatalogModel> Models);

    private string? CachePath(FoundryCatalogDevice device) =>
        _cacheDir is null ? null : Path.Combine(_cacheDir, $"foundry-catalog-{device.ToString().ToLowerInvariant()}.json");

    private WebCatalogResult? ReadCache(FoundryCatalogDevice device)
    {
        var path = CachePath(device);
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path), CacheJson);
            return file is null || file.Models.Count == 0 || !string.Equals(file.ApiUrl, _apiUrl, StringComparison.Ordinal)
                ? null
                : new WebCatalogResult(true, file.Models, _apiUrl, null, true, file.FetchedUtc);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private void WriteCache(FoundryCatalogDevice device, IReadOnlyList<FoundryCatalogModel> models, DateTimeOffset fetched)
    {
        var path = CachePath(device);
        if (path is null || models.Count == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new CacheFile(_apiUrl, fetched, models.ToList()), CacheJson));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache only saves a round trip; failing to write it is fine.
        }
    }

    private static JsonElement? Walk(JsonElement element, string[] path)
    {
        var current = element;
        foreach (var key in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(key, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static string? Str(JsonElement element, params string[] path) =>
        Walk(element, path) is { } e
            ? e.ValueKind switch
            {
                JsonValueKind.String => e.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => e.GetRawText(),
                _ => null,
            }
            : null;

    private static long? Long(JsonElement element, params string[] path) =>
        Walk(element, path) is { } e
            ? e.ValueKind switch
            {
                JsonValueKind.Number when e.TryGetInt64(out var n) => n,
                JsonValueKind.String when long.TryParse(e.GetString(), out var s) => s,
                _ => null,
            }
            : null;
}

// What OpenMono may load: `foundry model list` when Foundry is installed (the
// authority for that install), otherwise the public catalog. Metadata (size,
// modalities) always comes from the public catalog when it answered.
public sealed class FoundryCatalogView
{
    public FoundryCatalogView(IReadOnlyList<string> localAliases, WebCatalogResult? web)
    {
        LocalAliases = localAliases;
        Web = web;
    }

    public IReadOnlyList<string> LocalAliases { get; }

    public WebCatalogResult? Web { get; }

    public IReadOnlyList<FoundryCatalogModel> WebModels => Web?.Models ?? [];

    public bool HasLocal => LocalAliases.Count > 0;

    public bool HasWeb => WebModels.Count > 0;

    public bool Known => HasLocal || HasWeb;

    public string Source => HasLocal ? "foundry model list" : HasWeb ? Web!.Source : "no catalog";

    public string? Resolve(string alias) =>
        HasLocal ? FoundryAliasResolver.Resolve(alias, LocalAliases)
        : HasWeb ? FoundryAliasResolver.Resolve(alias, WebModels.Select(m => m.Alias))
        : null;

    public FoundryCatalogModel? Describe(string alias)
    {
        var match = FoundryAliasResolver.Resolve(alias, WebModels.Select(m => m.Alias));
        return match is null ? null : WebModels.First(m => string.Equals(m.Alias, match, StringComparison.Ordinal));
    }
}
