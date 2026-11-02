using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenMono.Permissions;

namespace OpenMono.Tools;

public sealed partial class WebSearchTool : ToolBase
{
    public override string Name => "WebSearch";
    public override string Description =>
        "Search the web. Returns titles, URLs, and snippets for the top results. " +
        "If the web search is blocked or returns no results, keyless Wikipedia results are " +
        "automatically appended (tagged '(wikipedia)') so you still get a real answer. " +
        "For stable reference/encyclopedic content, you can also fetch a specific article " +
        "directly with WebFetch (e.g. https://en.wikipedia.org/wiki/<topic>).";
    public override bool IsConcurrencySafe => true;
    public override bool IsReadOnly => true;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("query", "The search query")
        .AddInteger("max_results", "Maximum number of results (default: 8, max: 20)")
        .Require("query");

    private readonly HttpClient _http;

    public WebSearchTool()
        : this(new SocketsHttpHandler())
    {
    }

    // Allows tests to inject a fake HttpMessageHandler to drive the blocked / empty /
    // success paths deterministically without hitting the network.
    internal WebSearchTool(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler)
        {
            // No default User-Agent: it is set per-request (SearxngSearchAsync and
            // TryEndpointAsync each choose their own), because mixing a default with a
            // per-request header would emit a duplicate User-Agent line.
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    // Identifying UA for our own gateway traffic.
    private const string AgentUserAgent = "OpenMono.ai/0.1 (coding-agent)";

    // Browser-like UA used for the DuckDuckGo fallback endpoints. DDG flags the
    // agent UA ("OpenMono.ai/0.1 (coding-agent)") and, when its IP-level
    // rate-limiting kicks in, returns a bot-challenge page. A standard browser UA
    // is less likely to be flagged and is what most public parsers send.
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private static readonly (string Name, string Url)[] DuckDuckGoEndpoints =
    [
        ("html", "https://html.duckduckgo.com/html/"),
        ("lite", "https://lite.duckduckgo.com/lite/"),
    ];

    public override IReadOnlyList<Capability> RequiredCapabilities(JsonElement input) =>
    [
        new NetworkEgressCap("duckduckgo.com", 443, "https"),
        new NetworkEgressCap("html.duckduckgo.com", 443, "https"),
        new NetworkEgressCap("lite.duckduckgo.com", 443, "https"),
        new NetworkEgressCap("en.wikipedia.org", 443, "https"),
    ];

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

        return await FallbackWithWikipediaAnchorAsync(query, maxResults, context, ct);
    }

    /// <summary>
    /// The no-key fallback path. Runs the DuckDuckGo search, and — when it yields
    /// nothing useful (blocked / error / true-empty) — merges a few keyless Wikipedia
    /// REST results so the model still gets a real answer instead of a dead end. The
    /// anchor is best-effort: any failure is swallowed and the original (honest) result
    /// is returned, so it can never turn an error into a false success.
    /// </summary>
    private async Task<ToolResult> FallbackWithWikipediaAnchorAsync(
        string query, int maxResults, ToolContext context, CancellationToken ct)
    {
        var result = await DuckDuckGoSearchAsync(query, maxResults, context.OnDebug, ct);

        if (result.Class == ResultClass.Success)
            return result;

        // Only merge the anchor into a genuine web-search failure/empty — never into a
        // success. A success already has real results to surface.
        if (result.Class is not (ResultClass.Empty or ResultClass.InvalidInput))
            return result;

        if (!context.Config.Web.WikipediaAnchorEnabled)
            return result;

        List<SearchResult>? wiki;
        try
        {
            wiki = await WikipediaSearchAsync(query, WikiResultLimit, ct);
        }
        catch
        {
            // The anchor is optional enrichment; a failure must not mask the real result.
            context.OnDebug?.Invoke("WebSearch: Wikipedia anchor unavailable");
            return result;
        }

        if (wiki is { Count: > 0 })
        {
            // Keep the honest web-search outcome (blocked/empty message) and append the
            // anchor results below it, so the model still sees a real answer.
            var combined = new System.Text.StringBuilder()
                .Append(result.ModelPreview.TrimEnd())
                .AppendLine()
                .AppendLine()
                .Append(RenderResults(query, wiki).ModelPreview)
                .AppendLine()
                .Append("(Wikipedia results are added because the web search returned no usable results; they are a reliable keyless fallback.)");
            return ToolResult.Success(combined.ToString().TrimEnd());
        }

        return result;
    }

    private static int WikiResultLimit => 3;

    /// <summary>
    /// Keyless Wikipedia REST search. The only no-key general source that is never
    /// bot-walled from a datacenter IP, so it anchors the fallback. Returns real
    /// article titles, canonical URLs, and short excerpts (HTML match spans stripped).
    /// </summary>
    private async Task<List<SearchResult>> WikipediaSearchAsync(string query, int maxResults, CancellationToken ct)
    {
        var url = "https://en.wikipedia.org/w/rest.php/v1/search/page?q=" +
                  Uri.EscapeDataString(query) + "&limit=" + maxResults;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("User-Agent", AgentUserAgent);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<SearchResult>();
        foreach (var page in pages.EnumerateArray())
        {
            if (results.Count >= maxResults) break;

            var title = page.TryGetProperty("title", out var t) ? t.GetString() : null;
            var key = page.TryGetProperty("key", out var k) ? k.GetString() : null;
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(key)) continue;

            var excerpt = page.TryGetProperty("excerpt", out var e) ? e.GetString() : null;
            var clean = string.IsNullOrWhiteSpace(excerpt) ? null
                : WebUtility.HtmlDecode(StripTags().Replace(excerpt, "")).Trim();

            var urlValue = $"https://en.wikipedia.org/wiki/{key}";
            results.Add(new SearchResult { Title = $"{title} (wikipedia)", Url = urlValue, Snippet = clean });
        }

        return results;
    }

    private async Task<ToolResult> SearxngSearchAsync(
        string gateway, string? apiKey, string query, int maxResults, CancellationToken ct)
    {
        var url = $"{gateway.TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}&format=json";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("User-Agent", AgentUserAgent);
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _http.SendAsync(request, ct);
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

    /// <summary>
    /// Fallback search path. Tries each DuckDuckGo endpoint in order and returns
    /// the first one that yields real results.
    ///
    /// Outcomes are reported by <see cref="ResultClass"/>:
    /// <see cref="ToolResult.Success"/> ONLY when >= 1 real result was parsed;
    /// <see cref="ToolResult.Error"/> when the provider refused the request (bot
    /// challenge, HTTP 4xx/5xx, 401/403/429, timeout, network failure) — this fixes
    /// the old bug where a 202 bot-challenge page (202 is a 2xx, so it passed
    /// <c>EnsureSuccessStatusCode</c>) was silently reported as "No results found";
    /// and <see cref="ToolResult.Empty"/> when the request succeeded and the page was
    /// clean but contained zero results — a true empty, distinct from an error.
    /// </summary>
    private async Task<ToolResult> DuckDuckGoSearchAsync(
        string query, int maxResults, Action<string>? onDebug, CancellationToken ct)
    {
        var failureNotes = new List<string>();
        var blocked = false;
        var hardFailure = false;

        foreach (var (name, baseUrl) in DuckDuckGoEndpoints)
        {
            ct.ThrowIfCancellationRequested();
            var outcome = await TryEndpointAsync(baseUrl, query, maxResults, ct);

            if (outcome.Results.Count > 0)
            {
                if (name != DuckDuckGoEndpoints[0].Name)
                    // The primary endpoint failed and a fallback succeeded — note it for debuggability.
                    onDebug?.Invoke($"WebSearch: primary DDG endpoint unavailable; fell back to '{name}' endpoint.");
                return RenderResults(query, outcome.Results);
            }

            switch (outcome.Failure)
            {
                case EndpointFailure.Blocked:
                    blocked = true;
                    failureNotes.Add($"'{name}' endpoint blocked ({outcome.Detail ?? "bot challenge"})");
                    break;
                case EndpointFailure.HttpError:
                    hardFailure = true;
                    failureNotes.Add($"'{name}' endpoint failed ({outcome.Detail})");
                    break;
                case EndpointFailure.Timeout:
                    hardFailure = true;
                    failureNotes.Add($"'{name}' endpoint timed out");
                    break;
                case EndpointFailure.Network:
                    hardFailure = true;
                    failureNotes.Add($"'{name}' endpoint unreachable ({outcome.Detail})");
                    break;
                // EndpointFailure.None: the request succeeded and the page was clean
                // but contained no parseable results — a true empty. Nothing to note;
                // it is not an error.
            }
        }

        if (blocked)
            return ToolResult.Error(
                $"Web search returned no results — {string.Join("; ", failureNotes)}. The DuckDuckGo " +
                "fallback is rate-limited or blocked from this network (or its markup changed). " +
                "Try again, or configure a search gateway (SearXNG) via 'openmono setup search' for a reliable result.");

        if (hardFailure)
            return ToolResult.Error(
                $"Web search returned no results — {string.Join("; ", failureNotes)}. The DuckDuckGo " +
                "fallback could not be reached or returned an error. Try again, or configure a " +
                "search gateway (SearXNG) via 'openmono setup search'.");

        // Every endpoint returned a clean, parseable page with zero results.
        return ToolResult.Empty($"No results found for: {query}.");
    }

    private async Task<EndpointOutcome> TryEndpointAsync(
        string baseUrl, string query, int maxResults, CancellationToken ct)
    {
        try
        {
            var url = $"{baseUrl}?q={Uri.EscapeDataString(query)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "text/html");
            request.Headers.Add("User-Agent", BrowserUserAgent);

            using var response = await _http.SendAsync(request, ct);
            var statusCode = (int)response.StatusCode;
            var html = await response.Content.ReadAsStringAsync(ct);

            if (IsBotBlocked(statusCode, html))
                return new EndpointOutcome(Results: [], StatusCode: statusCode, Failure: EndpointFailure.Blocked,
                    Detail: statusCode is 401 or 403 ? $"HTTP {statusCode}" : "bot challenge");

            if (!response.IsSuccessStatusCode)
                // Non-2xx response (e.g. 401/403/429/5xx) — the provider rejected the request.
                return new EndpointOutcome(Results: [], StatusCode: statusCode, Failure: EndpointFailure.HttpError, Detail: $"HTTP {statusCode}");

            // Try the class-based parser (result__a / result__snippet — the "lite"
            // endpoint and the older "html" markup) first, then fall back to a
            // structural parser (h2 > a — the current "html" markup, which dropped
            // the result__a class). This keeps the parser working across DDG's
            // markup changes.
            var results = ParseResultsClassBased(html, maxResults);
            if (results.Count == 0)
                results = ParseResultsStructural(html, maxResults);

            // The request succeeded and the page is clean; zero results is a true empty.
            return new EndpointOutcome(Results: results, StatusCode: statusCode, Failure: EndpointFailure.None, Detail: null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient.Timeout elapsed (15s) — a per-endpoint timeout, not user cancellation.
            return new EndpointOutcome(Results: [], StatusCode: 0, Failure: EndpointFailure.Timeout, Detail: "15s timeout");
        }
        catch (HttpRequestException ex)
        {
            return new EndpointOutcome(Results: [], StatusCode: 0, Failure: EndpointFailure.Network, Detail: ex.Message);
        }
    }

    /// <summary>
    /// Detects DDG's bot-challenge page. The challenge is returned as HTTP 202
    /// (a 2xx) so a plain <c>IsSuccessStatusCode</c> check cannot catch it — the
    /// body is the only reliable signal. The page contains "bots use DuckDuckGo",
    /// "Select all squares containing a duck", "challenge", "square" and — critically —
    /// none of the <c>result__*</c> result classes.
    /// </summary>
    internal static bool IsBotBlocked(int statusCode, string html)
    {
        if (statusCode == 401 || statusCode == 403)
            return true;

        // 2xx status with a challenge/captcha body. 202 is DDG's canonical
        // bot-challenge status; 400/429 are defensive catches for other gating
        // states.
        if (statusCode is not (>= 200 and <= 299) and not (400 or 429))
            return false;

        // A page that has real result markup is a genuine result page, not a
        // challenge — even if it happens to mention "challenge" in text.
        if (html.Contains("result__a", StringComparison.Ordinal) ||
            html.Contains("result__snippet", StringComparison.Ordinal) ||
            html.Contains("result__title", StringComparison.Ordinal) ||
            html.Contains("result__body", StringComparison.Ordinal))
            return false;

        return html.Contains("bots use DuckDuckGo", StringComparison.OrdinalIgnoreCase)
            || html.Contains("select all squares", StringComparison.OrdinalIgnoreCase)
            || html.Contains("containing a duck", StringComparison.OrdinalIgnoreCase)
            || html.Contains("challenge to confirm", StringComparison.OrdinalIgnoreCase);
    }

    private static ToolResult RenderResults(string query, List<SearchResult> results)
    {
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

    // ---- Class-based parser (result__a / result__snippet — "lite" + older "html") ----
    // Attribute-order-independent: the opening tag is captured whole, then href is
    // extracted from it separately, so it works whether class= or href= comes first.

    internal static List<SearchResult> ParseResultsClassBased(string html, int maxResults)
    {
        var results = new List<SearchResult>();

        foreach (Match match in ResultLinkPattern().Matches(html))
        {
            if (results.Count >= maxResults) break;

            var title = WebUtility.HtmlDecode(StripTags().Replace(match.Groups["title"].Value, "")).Trim();
            var url = ResolveResultUrl(ExtractHref(match.Groups["tag"].Value));
            if (string.IsNullOrEmpty(title) || url is null) continue;

            results.Add(new SearchResult { Title = title, Url = url });
        }

        return AttachSnippets(results, html);
    }

    // ---- Structural parser (h2 > a — current "html" markup per ddgs v9.16.1) ----

    internal static List<SearchResult> ParseResultsStructural(string html, int maxResults)
    {
        var results = new List<SearchResult>();

        foreach (Match match in StructuredResultPattern().Matches(html))
        {
            if (results.Count >= maxResults) break;

            var title = WebUtility.HtmlDecode(StripTags().Replace(match.Groups["title"].Value, "")).Trim();
            var url = ResolveResultUrl(ExtractHref(match.Groups["tag"].Value));
            if (string.IsNullOrEmpty(title) || url is null) continue;

            results.Add(new SearchResult { Title = title, Url = url });
        }

        return AttachSnippets(results, html);
    }

    /// <summary>
    /// Snippets are not nested inside the result block in either markup, so they are
    /// paired positionally in document order — a safe approximation that matches DDG's
    /// one-snippet-per-result layout.
    /// </summary>
    private static List<SearchResult> AttachSnippets(List<SearchResult> results, string html)
    {
        var snippetMatches = SnippetPattern().Matches(html);
        for (var i = 0; i < Math.Min(snippetMatches.Count, results.Count); i++)
        {
            var snippet = WebUtility.HtmlDecode(
                StripTags().Replace(snippetMatches[i].Groups[1].Value, "")).Trim();
            results[i] = results[i] with { Snippet = snippet };
        }
        return results;
    }

    private static string? ExtractHref(string openingTag)
    {
        var m = HrefPattern().Match(openingTag);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    private static string DecodeUddg(string href) =>
        href.StartsWith("//duckduckgo.com/l/?uddg=", StringComparison.Ordinal)
            ? Uri.UnescapeDataString(href.Split("uddg=")[^1].Split('&')[0])
            : href;

    /// <summary>
    /// Resolves a raw result href to its real target URL, or null when the link should
    /// be skipped. Crucially the DuckDuckGo-internal filter runs AFTER uddg decoding —
    /// uddg redirect links (//duckduckgo.com/l/?uddg=...) live on duckduckgo.com but
    /// point at genuine results, so filtering before decoding would drop them.
    /// </summary>
    private static string? ResolveResultUrl(string? href)
    {
        if (string.IsNullOrEmpty(href)) return null;
        var url = DecodeUddg(href);
        if (url.Contains("duckduckgo.com")) return null;
        if (!url.StartsWith("http", StringComparison.Ordinal)) return null;
        return url;
    }

    // <a ...> where the tag carries class="result__a"; captures the tag and the inner text.
    [GeneratedRegex(@"<a\b(?<tag>[^>]*\bclass=""result__a""[^>]*)>(?<title>.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex ResultLinkPattern();

    [GeneratedRegex(@"<a\b[^>]*\bclass=""result__snippet""[^>]*>(?<text>.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex SnippetPattern();

    // <h2> ... <a href=...>title</a> — the current "html" result block.
    [GeneratedRegex(@"<h2[^>]*>\s*<a\b(?<tag>[^>]*>)(?<title>.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex StructuredResultPattern();

    [GeneratedRegex(@"href=""([^""]*)""")]
    private static partial Regex HrefPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex StripTags();

    internal sealed record SearchResult
    {
        public required string Title { get; init; }
        public required string Url { get; init; }
        public string? Snippet { get; init; }
    }

    private enum EndpointFailure { None, Blocked, HttpError, Timeout, Network }

    private sealed record EndpointOutcome(
        List<SearchResult> Results,
        int StatusCode,
        EndpointFailure Failure,
        string? Detail);
}
