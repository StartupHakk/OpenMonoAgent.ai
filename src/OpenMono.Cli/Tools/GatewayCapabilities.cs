using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenMono.Config;

namespace OpenMono.Tools;

public static class GatewayCapabilities
{
    public enum WebService { Search, Scrape }

    private readonly record struct Capabilities(bool Search, bool Scrape);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private static readonly ConcurrentDictionary<string, Task<Capabilities>> Cache = new();

    public static string? ResolveGateway(AppConfig config) =>
        !string.IsNullOrEmpty(config.Web.Gateway) ? config.Web.Gateway : config.Llm.Endpoint;

    public static async Task<bool> IsEnabledAsync(
        AppConfig config, WebService service, CancellationToken ct)
    {
        var configOverride = service == WebService.Search
            ? config.Web.SearchEnabled
            : config.Web.ScrapeEnabled;
        if (configOverride.HasValue)
            return configOverride.Value;

        var gateway = ResolveGateway(config);
        if (string.IsNullOrEmpty(gateway))
            return false;

        var caps = await ProbeAsync(gateway, config.Llm.ApiKey).WaitAsync(ct);
        return service == WebService.Search ? caps.Search : caps.Scrape;
    }

    private static Task<Capabilities> ProbeAsync(string gateway, string? apiKey) =>
        Cache.GetOrAdd(gateway.TrimEnd('/'), g => FetchAsync(g, apiKey));

    private static async Task<Capabilities> FetchAsync(string gateway, string? apiKey)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{gateway}/services");
            if (!string.IsNullOrEmpty(apiKey))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return default;

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new Capabilities(
                Search: IsTrue(root, "search"),
                Scrape: IsTrue(root, "scrape"));
        }
        catch
        {
            return default;
        }
    }

    private static bool IsTrue(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => el.GetString()?.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on",
            _ => false,
        };
}
