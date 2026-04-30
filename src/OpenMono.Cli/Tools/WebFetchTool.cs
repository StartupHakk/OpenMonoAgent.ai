using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenMono.Permissions;

namespace OpenMono.Tools;

public sealed partial class WebFetchTool : ToolBase
{
    public override string Name => "WebFetch";
    public override string Description => "Fetch a web page and extract its text content. Returns the page text with HTML tags stripped.";
    public override bool IsConcurrencySafe => true;
    public override bool IsReadOnly => true;

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddString("url", "The URL to fetch")
        .AddInteger("max_length", "Maximum characters to return (default: 20000)")
        .AddProperty("headers", new
        {
            type = "object",
            description = "Optional HTTP headers to send",
            additionalProperties = new { type = "string" }
        })
        .Require("url");

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders =
        {
            { "User-Agent", "OpenMono.ai/0.1 (coding-agent)" },
            { "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,text/plain;q=0.8,*/*;q=0.7" },
        }
    };

    public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input)
    {
        var url = input.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(url))
            return [];
        return [NetworkEgressCap.FromUrl(url)];
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var url = input.GetProperty("url").GetString()!;
        var maxLength = input.TryGetProperty("max_length", out var ml) ? ml.GetInt32() : 20_000;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            return ToolResult.Error($"Invalid URL: {url}");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            if (input.TryGetProperty("headers", out var headers))
            {
                foreach (var header in headers.EnumerateObject())
                    request.Headers.TryAddWithoutValidation(header.Name, header.Value.GetString());
            }

            using var response = await Http.SendAsync(request, ct);
            var statusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
                return ToolResult.Error($"HTTP {statusCode} {response.StatusCode} for {url}");

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            var body = await response.Content.ReadAsStringAsync(ct);

            string text;
            if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                text = ExtractTextFromHtml(body);
            else
                text = body;

            if (text.Length > maxLength)
                text = text[..maxLength] + $"\n\n... (truncated at {maxLength} chars, total: {text.Length})";

            return ToolResult.Success($"[{statusCode}] {url} ({text.Length} chars)\n\n{text}");
        }
        catch (TaskCanceledException)
        {
            return ToolResult.Error($"Request timed out (30s): {url}");
        }
        catch (HttpRequestException ex)
        {
            return ToolResult.Error($"HTTP error fetching {url}: {ex.Message}");
        }
    }

    private static string ExtractTextFromHtml(string html)
    {

        var cleaned = ScriptPattern().Replace(html, " ");
        cleaned = StylePattern().Replace(cleaned, " ");

        cleaned = TagPattern().Replace(cleaned, " ");

        cleaned = WebUtility.HtmlDecode(cleaned);

        cleaned = WhitespacePattern().Replace(cleaned, " ");

        var lines = cleaned.Split('\n', StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0);

        return string.Join('\n', lines).Trim();
    }

    [GeneratedRegex(@"<script[^>]*>[\s\S]*?</script>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptPattern();

    [GeneratedRegex(@"<style[^>]*>[\s\S]*?</style>", RegexOptions.IgnoreCase)]
    private static partial Regex StylePattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex WhitespacePattern();
}
