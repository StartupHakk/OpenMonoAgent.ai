using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenMono.Permissions;

namespace OpenMono.Tools;

public sealed partial class WebSearchTool : ToolBase
{
    public override string Name => "WebSearch";
    public override string Description => "Search the web using DuckDuckGo. Returns titles, URLs, and snippets for the top results.";
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

    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input) =>
        [new NetworkEgressCap("duckduckgo.com", 443, "https")];

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var query = input.GetProperty("query").GetString()!;
        var maxResults = input.TryGetProperty("max_results", out var mr) ? Math.Min(mr.GetInt32(), 20) : 8;

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
