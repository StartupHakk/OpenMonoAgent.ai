using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
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
        .AddBoolean("render", "Force a real browser instead of a plain HTTP fetch (slower; for JS-heavy or bot-protected pages). Only applies when the Scrapling gateway is enabled. Default: false")
        .AddBoolean("headless", "When the browser is used, run it headless. Set false for headed mode (requires a display on the scrape host). Default: true")
        .AddProperty("headers", new
        {
            type = "object",
            description = "Optional HTTP headers to send",
            additionalProperties = new { type = "string" }
        })
        .Require("url");

    private static readonly HttpClient Http = CreateGuardedClient();

    private static HttpClient CreateGuardedClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            ConnectCallback = GuardedConnectAsync,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("User-Agent", "OpenMono.ai/0.1 (coding-agent)");
        client.DefaultRequestHeaders.Add(
            "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,text/plain;q=0.8,*/*;q=0.7");
        return client;
    }

    private static async ValueTask<Stream> GuardedConnectAsync(
        SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        var target = addresses.FirstOrDefault(a => !IsBlockedAddress(a));
        if (target is null)
            throw new HttpRequestException(
                $"Blocked: '{host}' resolves to a private, loopback, or link-local address — " +
                "refusing to connect (SSRF protection).");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(target, ctx.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static readonly HttpClient ScrapeHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(90),
    };

    public override IReadOnlyList<Capability> RequiredCapabilities(JsonElement input)
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
        var render = input.TryGetProperty("render", out var r) && r.ValueKind == JsonValueKind.True;
        var headless = !input.TryGetProperty("headless", out var h) || h.ValueKind != JsonValueKind.False;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
            return ToolResult.Error($"Invalid URL: {url}");

        if (await GatewayCapabilities.IsEnabledAsync(context.Config, GatewayCapabilities.WebService.Scrape, ct))
        {
            var gateway = GatewayCapabilities.ResolveGateway(context.Config)!;
            try
            {
                return await ScraplingFetchAsync(gateway, context.Config.Llm.ApiKey, url, maxLength, render, headless, ct);
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) throw;
                context.OnDebug?.Invoke($"WebFetch: Scrapling gateway unavailable ({ex.Message}); falling back to direct fetch");
            }
        }

        return await DirectFetchAsync(uri, url, input, maxLength, ct);
    }

    private static async Task<ToolResult> ScraplingFetchAsync(
        string gateway, string? apiKey, string url, int maxLength, bool render, bool headless, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            url,
            max_length = maxLength,
            format = "markdown",
            render,
            headless,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{gateway.TrimEnd('/')}/scrape")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await ScrapeHttp.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number
            ? s.GetInt32() : 200;

        if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
            throw new HttpRequestException($"scrape error: {errEl.GetString()}");

        var content = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        if (content.Length > maxLength)
            content = content[..maxLength] + $"\n\n... (truncated at {maxLength} chars)";

        return ToolResult.Success($"[{status}] {url} ({content.Length} chars)\n\n{content}");
    }

    private static async Task<ToolResult> DirectFetchAsync(
        Uri uri, string url, JsonElement input, int maxLength, CancellationToken ct)
    {
        try
        {
            IPAddress[] addresses;
            try
            {
                addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                return ToolResult.Error($"Could not resolve host for {url}: {ex.Message}");
            }

            if (addresses.Length == 0 || addresses.Any(IsBlockedAddress))
                return ToolResult.Error(
                    $"Blocked: {url} resolves to a private, loopback, or link-local address — " +
                    "refusing to fetch (SSRF protection).");

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

    private static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 0) return true;
            if (b[0] == 10) return true;
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            if (b[0] == 192 && b[1] == 168) return true;
            if (b[0] == 169 && b[1] == 254) return true;
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;

        return false;
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
