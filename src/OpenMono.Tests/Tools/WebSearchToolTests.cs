using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Tools;

public class WebSearchToolTests
{
    // --- Markup fixtures ---------------------------------------------------------

    // "lite" / older "html" markup: class-based (result__a / result__snippet).
    // Note the second result has href BEFORE class — both orders must parse.
    private const string LiteHtml = """
        <html><body>
          <a class="result__a" rel="nofollow" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fabout&amp;rut=x">Example Page</a>
          <a class="result__snippet">About example page &amp; more.</a>
          <a rel="nofollow" href="https://second.org/page" class="result__a">Second Result</a>
          <a class="result__snippet">Second snippet.</a>
        </body></html>
        """;

    // Current "html" markup: structural (h2 > a, no result__a class).
    private const string HtmlMarkup = """
        <html><body>
          <div class="result results_links web-result">
            <div class="body">
              <h2 class="result__title"><a rel="nofollow" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fmodsite.com%2Fa&amp;rut=y">Modern Site</a></h2>
            </div>
          </div>
          <a class="result__snippet">Modern snippet here.</a>
        </body></html>
        """;

    // Clean 200 page with no result markup — a true empty.
    private const string EmptyHtml = """
        <html><body><div class="results">No results</div></body></html>
        """;

    // DDG bot-challenge page (served as HTTP 202).
    private const string ChallengeHtml = """
        <html><head><title>DuckDuckGo</title></head><body>
          <p>bots use DuckDuckGo</p>
          <p>Select all squares containing a duck</p>
          <div class="challenge">challenge to confirm you are human</div>
        </body></html>
        """;

    // --- Infrastructure ----------------------------------------------------------

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_responder(request));
    }

    private static HttpResponseMessage Response(int status, string body)
        => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static WebSearchTool Tool(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new FakeHttpHandler(responder));

    private static ToolContext CreateContext()
    {
        // Web.Search = "false" makes GatewayCapabilities.IsEnabledAsync return false
        // immediately (no network probe), so the tool always takes the DuckDuckGo
        // fallback path — which is exactly what these tests drive.
        var config = new AppConfig { WorkingDirectory = "/tmp" };
        config.Web.Search = "false";
        return new ToolContext
        {
            ToolRegistry = new ToolRegistry(),
            Session = new SessionState(),
            Permissions = new PermissionEngine(config, new TerminalRenderer(), new TerminalRenderer()),
            Config = config,
            WorkingDirectory = "/tmp",
            WriteOutput = _ => { },
            AskUser = (_, _) => Task.FromResult(""),
        };
    }

    private static JsonElement Input(string query, int? maxResults = null)
    {
        var json = maxResults is null
            ? $"{{\"query\":\"{query}\"}}"
            : $"{{\"query\":\"{query}\",\"max_results\":{maxResults.Value}}}";
        return JsonDocument.Parse(json).RootElement;
    }

    // --- IsBotBlocked ----------------------------------------------------------

    [Fact]
    public void IsBotBlocked_202WithChallengeText_ReturnsTrue()
        => WebSearchTool.IsBotBlocked(202, ChallengeHtml).Should().BeTrue();

    [Fact]
    public void IsBotBlocked_403_ReturnsTrue()
        => WebSearchTool.IsBotBlocked(403, "").Should().BeTrue();

    [Fact]
    public void IsBotBlocked_200WithResultClasses_ReturnsFalse()
        => WebSearchTool.IsBotBlocked(200, LiteHtml).Should().BeFalse();

    [Fact]
    public void IsBotBlocked_404_ReturnsFalse()
        => WebSearchTool.IsBotBlocked(404, "").Should().BeFalse();

    [Fact]
    public void IsBotBlocked_500_ReturnsFalse()
        => WebSearchTool.IsBotBlocked(500, "Internal error").Should().BeFalse();

    // --- Parsers -------------------------------------------------------------

    [Fact]
    public void ParseResultsClassBased_ParsesTitlesSnippetsAndUddg()
    {
        var results = WebSearchTool.ParseResultsClassBased(LiteHtml, 8);

        results.Should().HaveCount(2);
        results[0].Title.Should().Be("Example Page");
        // uddg= wrapper is unwrapped to the real target URL.
        results[0].Url.Should().Be("https://example.com/about");
        results[0].Snippet.Should().Be("About example page & more.");
        // href-before-class order is also parsed.
        results[1].Title.Should().Be("Second Result");
        results[1].Url.Should().Be("https://second.org/page");
        results[1].Snippet.Should().Be("Second snippet.");
    }

    [Fact]
    public void ParseResultsStructural_ParsesH2Links()
    {
        var results = WebSearchTool.ParseResultsStructural(HtmlMarkup, 8);

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Modern Site");
        results[0].Url.Should().Be("https://modsite.com/a");
        results[0].Snippet.Should().Be("Modern snippet here.");
    }

    [Fact]
    public void ParseResultsClassBased_OnStructuralMarkup_ReturnsEmpty()
        => WebSearchTool.ParseResultsClassBased(HtmlMarkup, 8).Should().BeEmpty();

    [Fact]
    public void ParseResultsStructural_OnLiteMarkup_ReturnsEmpty()
        => WebSearchTool.ParseResultsStructural(LiteHtml, 8).Should().BeEmpty();

    [Fact]
    public void ParseResultsStructural_SkipsDuckDuckGoInternalLinks()
    {
        var html = "<h2><a href=\"https://duckduckgo.com/?q=test\">DDG link</a></h2>";
        WebSearchTool.ParseResultsStructural(html, 8).Should().BeEmpty();
    }

    [Fact]
    public void ParseResultsClassBased_RespectsMaxResults()
    {
        var html = string.Concat(Enumerable.Range(1, 5)
            .Select(i => $"""<a class="result__a" href="https://x{i}.com/p">T{i}</a>"""));
        WebSearchTool.ParseResultsClassBased(html, 3).Should().HaveCount(3);
    }

    // --- End-to-end: blocked / error vs empty vs success ----------------------

    [Fact]
    public async Task BlockedBothEndpoints_ReturnsErrorNotSuccess()
    {
        // Every request gets the 202 challenge page — the exact live scenario.
        var tool = Tool(_ => Response(202, ChallengeHtml));

        var result = await tool.ExecuteAsync(Input("csharp regex library"), CreateContext(), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Class.Should().Be(ResultClass.InvalidInput);
        result.ErrorMessage.Should().Contain("no results");
        result.ErrorMessage.Should().Contain("blocked");
        result.ErrorMessage.Should().NotContain("No results found for: csharp regex library");
    }

    [Fact]
    public async Task Http403_ReturnsError()
    {
        var tool = Tool(_ => Response(403, "Forbidden"));

        var result = await tool.ExecuteAsync(Input("test query"), CreateContext(), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ErrorMessage.Should().Contain("HTTP 403");
    }

    [Fact]
    public async Task CleanEmptyPage_ReturnsEmptyNotSuccess()
    {
        // A clean 200 page with zero results — a true empty, distinct from an error.
        var tool = Tool(_ => Response(200, EmptyHtml));

        var result = await tool.ExecuteAsync(Input("qwertyuiop asdfghjkl"), CreateContext(), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Class.Should().Be(ResultClass.Empty);
        result.ModelPreview.Should().Contain("No results found for: qwertyuiop asdfghjkl");
    }

    [Fact]
    public async Task LiteMarkup200_ReturnsSuccessWithResults()
    {
        var tool = Tool(_ => Response(200, LiteHtml));

        var result = await tool.ExecuteAsync(Input("test query"), CreateContext(), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Class.Should().Be(ResultClass.Success);
        result.Content.Should().Contain("Example Page");
        result.Content.Should().Contain("https://example.com/about");
        result.Content.Should().Contain("Second Result");
    }

    [Fact]
    public async Task StructuralMarkup200_ReturnsSuccessWithResults()
    {
        var tool = Tool(_ => Response(200, HtmlMarkup));

        var result = await tool.ExecuteAsync(Input("test query"), CreateContext(), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Modern Site");
        result.Content.Should().Contain("https://modsite.com/a");
    }

    [Fact]
    public async Task PrimaryBlockedFallbackSucceeds_ReturnsSuccess()
    {
        var tool = Tool(req => req.RequestUri!.Host.StartsWith("lite.")
            ? Response(200, LiteHtml)
            : Response(202, ChallengeHtml));

        var result = await tool.ExecuteAsync(Input("test query"), CreateContext(), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Example Page");
    }

    [Fact]
    public async Task NetworkFailure_ReturnsError()
    {
        var tool = new WebSearchTool(new ThrowingHandler());

        var result = await tool.ExecuteAsync(Input("test query"), CreateContext(), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ErrorMessage.Should().Contain("unreachable");
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("connection refused");
    }

    // --- Wikipedia anchor --------------------------------------------------

    private const string WikiJson =
        """
        {"pages":[
          {"title":"Coroutine","key":"Coroutine","excerpt":"A <span class=\"searchmatch\">Coroutine</span> is a callable computation that can be paused."},
          {"title":"Asynchronous programming","key":"Asynchronous_programming","excerpt":"Asynchronous programming is a form of computer programming."}
        ]}
        """;

    private static WebSearchTool ToolWithWiki(Func<HttpRequestMessage, HttpResponseMessage> ddg, string? wiki = WikiJson)
    {
        var tool = new WebSearchTool(new FakeHttpHandler(req =>
        {
            if (req.RequestUri!.Host == "en.wikipedia.org")
                return wiki is null
                    ? Response(500, "boom")
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(wiki, Encoding.UTF8, "application/json") };
            return ddg(req);
        }));
        return tool;
    }

    [Fact]
    public async Task BlockedDdgWithWikiResults_MergesAnchorAndSucceeds()
    {
        var tool = ToolWithWiki(_ => Response(202, ChallengeHtml));

        var result = await tool.ExecuteAsync(Input("python asyncio"), CreateContext(), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Class.Should().Be(ResultClass.Success);
        result.Content.Should().Contain("Coroutine");
        result.Content.Should().Contain("https://en.wikipedia.org/wiki/Coroutine");
        result.Content.Should().Contain("Asynchronous_programming");
        // The honest web-search outcome stays visible.
        result.Content.Should().Contain("blocked");
    }

    [Fact]
    public async Task EmptyDdgWithWikiResults_MergesAnchorAndSucceeds()
    {
        var tool = ToolWithWiki(_ => Response(200, EmptyHtml));

        var result = await tool.ExecuteAsync(Input("qwertyuiop"), CreateContext(), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("No results found for: qwertyuiop");
        result.Content.Should().Contain("https://en.wikipedia.org/wiki/Coroutine");
    }

    [Fact]
    public async Task BlockedDdgWithWikiFailure_KeepsOriginalError()
    {
        // Wiki endpoint 500s — the anchor must not mask the real blocked error.
        var tool = ToolWithWiki(_ => Response(202, ChallengeHtml), wiki: null);

        var result = await tool.ExecuteAsync(Input("python asyncio"), CreateContext(), CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Class.Should().Be(ResultClass.InvalidInput);
        result.ErrorMessage.Should().Contain("blocked");
        result.ErrorMessage.Should().NotContain("wikipedia");
    }

    [Fact]
    public async Task SuccessDdg_DoesNotConsultWikipedia()
    {
        var hitWiki = false;
        var tool = new WebSearchTool(new FakeHttpHandler(req =>
        {
            if (req.RequestUri!.Host == "en.wikipedia.org")
            {
                hitWiki = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(WikiJson, Encoding.UTF8, "application/json") };
            }
            return Response(200, LiteHtml);
        }));

        var result = await tool.ExecuteAsync(Input("test query"), CreateContext(), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Example Page");
        result.Content.Should().NotContain("wikipedia");
        hitWiki.Should().BeFalse("a successful web search must not consult the anchor");
    }

    [Fact]
    public async Task WikiAnchorDisabled_BlockedDdgStaysError()
    {
        var ctx = CreateContext();
        ctx.Config.Web.WikipediaAnchor = "false";

        var tool = ToolWithWiki(_ => Response(202, ChallengeHtml));

        var result = await tool.ExecuteAsync(Input("python asyncio"), ctx, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.ErrorMessage.Should().Contain("blocked");
        result.ErrorMessage.Should().NotContain("wikipedia");
    }
}
