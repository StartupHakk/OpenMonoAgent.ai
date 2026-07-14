using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenMono.Permissions;

namespace OpenMono.Tools;

public sealed partial class WebSearchTool : ToolBase
{
    public override string Name => "WebSearch";
    public override string Description => "Search the web. Returns titles, URLs, and snippets for the top results. " +
        "Use for general or current information; for library/framework API documentation prefer the Context7 tools if available.";
    public override bool IsConcurrencySafe => true;
    public override bool IsReadOnly => true;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("query", "The search query")
        .AddInteger("max_results", "Maximum number of results (default: 8, max: 20)")
        .Require("query");

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders =
        {
            { "User-Agent", "OpenMono.ai/0.1 (coding-agent)" },
        }
    };

    public override IReadOnlyList<Capability> RequiredCapabilities(JsonElement input) =>
        [new NetworkEgressCap("duckduckgo.com", 443, "https")];

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var query = input.GetProperty("query").GetString()!;
        var maxResults = input.TryGetProperty("max_results", out var mr) ? Math.Min(mr.GetInt32(), 20) : 8;

        if (await GatewayCapabilities.IsEnabledAsync(context.Config, GatewayCapabilities.WebService.Search, ct))
        {
            var gateway = GatewayCapabilities.ResolveGateway(context.Config)!;
            try
            {
                return await SearxngSearchAsync(gateway, context.Config.Llm.ApiKey, query, maxResults, ct);
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) throw;
                context.OnDebug?.Invoke($"WebSearch: SearXNG gateway unavailable ({ex.Message}); falling back to DuckDuckGo");
            }
        }

        return await DuckDuckGoSearchAsync(query, maxResults, ct);
    }

    private static async Task<ToolResult> SearxngSearchAsync(
        string gateway, string? apiKey, string query, int maxResults, CancellationToken ct)
    {
        var url = $"{gateway.TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}&format=json";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("results", out var resultsEl) ||
            resultsEl.ValueKind != JsonValueKind.Array)
            return ToolResult.Success($"No results found for: {query}");

        var output = new System.Text.StringBuilder();
        output.AppendLine($"Search results for: {query}\n");

        var count = 0;
        foreach (var r in resultsEl.EnumerateArray())
        {
            if (count >= maxResults) break;
            var title = r.TryGetProperty("title", out var t) ? t.GetString() : null;
            var resultUrl = r.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(resultUrl)) continue;

            var snippet = r.TryGetProperty("content", out var c) ? c.GetString() : null;

            count++;
            output.AppendLine($"{count}. {title}");
            output.AppendLine($"   {resultUrl}");
            if (!string.IsNullOrEmpty(snippet))
                output.AppendLine($"   {snippet}");
            output.AppendLine();
        }

        return count == 0
            ? ToolResult.Success($"No results found for: {query}")
            : ToolResult.Success(output.ToString().TrimEnd());
    }

    private static async Task<ToolResult> DuckDuckGoSearchAsync(string query, int maxResults, CancellationToken ct)
    {
        try
        {
            var encoded = Uri.EscapeDataString(query);
            var url = $"https://html.duckduckgo.com/html/?q={encoded}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "text/html");

            using var response = await Http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var html = await response.Content.ReadAsStringAsync(ct);

            var results = ParseResults(html, maxResults);

            if (results.Count == 0)
                return ToolResult.Success($"No results found for: {query}");

            var output = new System.Text.StringBuilder();
            output.AppendLine($"Search results for: {query}\n");

            for (var i = 0; i < results.Count; i++)
            {
                var r = results[i];
                output.AppendLine($"{i + 1}. {r.Title}");
                output.AppendLine($"   {r.Url}");
                if (!string.IsNullOrEmpty(r.Snippet))
                    output.AppendLine($"   {r.Snippet}");
                output.AppendLine();
            }

            return ToolResult.Success(output.ToString().TrimEnd());
        }
        catch (TaskCanceledException)
        {
            return ToolResult.Error($"Search timed out (15s): {query}");
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Error($"Search failed: {ex.Message}");
        }
    }

    private static List<SearchResult> ParseResults(string html, int maxResults)
    {
        var results = new List<SearchResult>();

        var linkMatches = ResultLinkPattern().Matches(html);

        foreach (Match match in linkMatches)
        {
            if (results.Count >= maxResults) break;

            var href = WebUtility.HtmlDecode(match.Groups[1].Value);
            var title = WebUtility.HtmlDecode(StripTags().Replace(match.Groups[2].Value, "")).Trim();

            if (string.IsNullOrEmpty(title) || href.Contains("duckduckgo.com")) continue;

            if (href.StartsWith("//duckduckgo.com/l/?uddg="))
            {
                var uddg = Uri.UnescapeDataString(href.Split("uddg=")[^1].Split('&')[0]);
                href = uddg;
            }

            results.Add(new SearchResult { Title = title, Url = href });
        }

        var snippetMatches = SnippetPattern().Matches(html);
        for (var i = 0; i < Math.Min(snippetMatches.Count, results.Count); i++)
        {
            var snippet = WebUtility.HtmlDecode(
                StripTags().Replace(snippetMatches[i].Groups[1].Value, "")).Trim();
            results[i] = results[i] with { Snippet = snippet };
        }

        return results;
    }

    [GeneratedRegex(@"<a[^>]*class=""result__a""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex ResultLinkPattern();

    [GeneratedRegex(@"<a[^>]*class=""result__snippet""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex SnippetPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex StripTags();

    private sealed record SearchResult
    {
        public required string Title { get; init; }
        public required string Url { get; init; }
        public string? Snippet { get; init; }
    }
}
