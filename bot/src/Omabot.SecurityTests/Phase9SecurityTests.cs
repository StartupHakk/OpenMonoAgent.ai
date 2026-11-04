// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Omabot.Approvals;
using Omabot.Cli;
using Omabot.Common;
using Omabot.Config;
using Omabot.Db;
using Omabot.Rooms;
using Omabot.Runtime;
using Omabot.Tests.Support;
using Omabot.Web;

namespace Omabot.SecurityTests;

/// <summary>Phase 9: the local web view's hardening (headers, Host, sign-in, CSRF, Origin, roles, escaping).</summary>
public sealed class Phase9SecurityTests : IAsyncLifetime
{
    private const string Team = """
        version: 1
        models:
          flash-next:
            upstream_model: Qwen3.8-Flash-Next
        rooms:
          - { name: core, members: [Scout, Builder], responder: mentions }
        bots:
          - name: Scout
            personality: You are Scout.
            model: flash-next
            tools: { allow: [FileRead] }
          - name: Builder
            personality: You are Builder.
            model: flash-next
            tools: { allow: [FileRead, FileWrite] }
        """;

    private readonly TempRoster _r = new();
    private RosterDb _db = null!;
    private WebAuth _auth = null!;
    private Microsoft.AspNetCore.Builder.WebApplication _app = null!;
    private int _port;
    private string Origin => $"http://127.0.0.1:{_port}";

    public async Task InitializeAsync()
    {
        var env = new CliEnv { Paths = _r.Paths, AppDir = _r.AppDir, Clock = _r.Clock, Out = new StringWriter(), Err = new StringWriter() };
        Assert.Equal(0, await new CliApp(env).RunAsync(["team", "apply", _r.TeamFile(Team)]));
        _db = _r.Db();
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        _r.Config.Api.Port = _port;
        _auth = new WebAuth(new UsersDb(_r.Paths.UsersDb), _r.Clock, TimeSpan.FromHours(12));
        var audit = _r.Audit(_db);
        var notices = new Notices(_db, _r.Clock, Redactor.Shared);
        var runtime = new AssignmentRuntime(_db, _r.Config, _r.Paths, _r.Clock, audit, notices, new TurnDriver(_db, _r.Clock, _r.Paths, _ => throw new InvalidOperationException()));
        var deps = new WebDeps
        {
            Db = _db, Config = _r.Config, Paths = _r.Paths, Clock = _r.Clock, Audit = audit, Notices = notices, Auth = _auth,
            Options = WebServer.Local(_r.Config), Rooms = new RoomService(_db, _r.Clock, audit, runtime),
        };
        _app = WebServer.Build(deps, IPAddress.Loopback, _port);
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _r.Dispose();
    }

    private HttpClient Client(CookieContainer? jar = null) => new(new HttpClientHandler { CookieContainer = jar ?? new CookieContainer(), UseCookies = true, AllowAutoRedirect = false })
    {
        BaseAddress = new Uri($"http://127.0.0.1:{_port}"),
    };

    private async Task<(HttpClient Http, string Csrf)> SignIn(string role = "owner")
    {
        var jar = new CookieContainer();
        var http = Client(jar);
        if (role == "owner")
        {
            var r = await http.GetAsync("/login?code=" + _auth.CreateLoginCode("local:spencer", "spencer", "owner", TimeSpan.FromMinutes(5)));
            Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        }
        else
        {
            var s = _auth.Open("local:guest", "guest", role, "127.0.0.1");
            jar.Add(new Uri($"http://127.0.0.1:{_port}"), new Cookie(WebAuth.Cookie, s.Id, "/"));
        }
        var session = await http.GetFromJsonAsync<JsonObject>("/api/session");
        return (http, session!["csrf"]!.GetValue<string>());
    }

    private HttpRequestMessage Post(string path, object body, string? csrf, string? origin)
    {
        var m = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (csrf is not null) m.Headers.Add("X-CSRF-Token", csrf);
        if (origin is not null) m.Headers.Add("Origin", origin);
        return m;
    }

    private ApprovalRow Card(string summary = "Builder wants to run Bash: dotnet test")
        => new ApprovalStore(_db, _r.Clock, Redactor.Shared).Create(new ApprovalRequest(ApprovalKinds.Permission, "builder", summary,
            new JsonObject { ["command"] = "dotnet test" }, "bot", "builder", SessionId: "ses_w", PauseId: "perm_" + Guid.NewGuid().ToString("N")[..6], Ttl: TimeSpan.FromMinutes(10), Action: "dotnet test"));

    [Fact(DisplayName = "Web view: strict CSP and hardening headers, no inline script, and the page never puts data in as HTML")]
    public async Task Headers()
    {
        using var http = Client();
        var r = await http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(WebServer.Csp, r.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("script-src 'self'", WebServer.Csp);
        Assert.DoesNotContain("unsafe", WebServer.Csp);
        Assert.Equal("DENY", r.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", r.Headers.GetValues("Referrer-Policy").Single());
        Assert.False(r.Headers.Contains("Server"));
        var html = await r.Content.ReadAsStringAsync();
        Assert.Empty(Regex.Matches(html, "<script(?![^>]*\\bsrc=)", RegexOptions.IgnoreCase));
        Assert.DoesNotContain(" style=", html);
        Assert.Empty(Regex.Matches(html, "\\son[a-z]+\\s*=", RegexOptions.IgnoreCase));
        var js = await http.GetStringAsync("/assets/app.js");
        foreach (var bad in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(", "new Function" })
            Assert.DoesNotContain(bad, js);
        Assert.Contains("textContent", js);
    }

    [Fact(DisplayName = "Web view: a request for another host name is refused (DNS rebinding)")]
    public async Task HostCheck()
    {
        using var http = Client();
        var m = new HttpRequestMessage(HttpMethod.Get, "/api/bots");
        m.Headers.Host = $"evil.example:{_port}";
        Assert.Equal((HttpStatusCode)421, (await http.SendAsync(m)).StatusCode);
        var page = new HttpRequestMessage(HttpMethod.Get, "/");
        page.Headers.Host = "attacker.test";
        Assert.Equal((HttpStatusCode)421, (await http.SendAsync(page)).StatusCode);
    }

    [Fact(DisplayName = "Web view: sign-in links work once, expire, and lock out guessing; the cookie is HttpOnly and SameSite=Strict")]
    public async Task SignInLinks()
    {
        using var anon = Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/bots")).StatusCode);
        var code = _auth.CreateLoginCode("local:spencer", "spencer", "owner", TimeSpan.FromMinutes(5));
        using var first = Client();
        var ok = await first.GetAsync("/login?code=" + code);
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        var cookie = ok.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie.ToLowerInvariant());
        Assert.Contains("samesite=strict", cookie.ToLowerInvariant());
        Assert.Contains("path=/", cookie.ToLowerInvariant());
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().GetAsync("/login?code=" + code)).StatusCode);
        var late = _auth.CreateLoginCode("local:spencer", "spencer", "owner", TimeSpan.FromMinutes(5));
        _r.Clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().GetAsync("/login?code=" + late)).StatusCode);
        var raw = cookie.Split(';')[0].Split('=')[1];
        Assert.DoesNotContain(raw, File.ReadAllText(_r.Paths.UsersDb));
        using var guesser = Client();
        for (var i = 0; i < 10; i++) await guesser.GetAsync("/login?code=olc_" + new string((char)('a' + i), 64));
        Assert.Equal((HttpStatusCode)429, (await guesser.GetAsync("/login?code=" + _auth.CreateLoginCode("local:spencer", "spencer", "owner", TimeSpan.FromMinutes(5)))).StatusCode);
    }

    [Fact(DisplayName = "Web view: a change needs the CSRF token and this page's Origin, and an answer must carry the card's own hash")]
    public async Task CsrfOriginAndHash()
    {
        var (http, csrf) = await SignIn();
        using var _ = http;
        var card = Card();
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Post($"/api/approvals/{card.Id}", new { decision = "allow", hash = card.ActionHash }, null, Origin))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Post($"/api/approvals/{card.Id}", new { decision = "allow", hash = card.ActionHash }, "wrong", Origin))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Post($"/api/approvals/{card.Id}", new { decision = "allow", hash = card.ActionHash }, csrf, "https://evil.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Post($"/api/approvals/{card.Id}", new { decision = "allow", hash = card.ActionHash }, csrf, null))).StatusCode);
        var other = Card("Builder wants to run Bash: rm -rf build");
        Assert.Equal(HttpStatusCode.Conflict, (await http.SendAsync(Post($"/api/approvals/{card.Id}", new { decision = "allow", hash = other.ActionHash }, csrf, Origin))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(Post($"/api/approvals/{card.Id}", new { decision = "allow" }, csrf, Origin))).StatusCode);
        var good = await http.SendAsync(Post($"/api/approvals/{card.Id}", new { decision = "allow", hash = card.ActionHash, reason = "fine" }, csrf, Origin));
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        var row = new ApprovalStore(_db, _r.Clock, Redactor.Shared).Get(card.Id)!;
        Assert.Equal(("allowed", "person", "spencer"), (row.Status, row.ApproverKind, row.Approver));
        Assert.Equal(HttpStatusCode.Conflict, (await http.SendAsync(Post($"/api/approvals/{card.Id}", new { decision = "deny", hash = card.ActionHash }, csrf, Origin))).StatusCode);
        Assert.Equal(1, Convert.ToInt32(_db.Scalar("SELECT COUNT(*) FROM audit WHERE actor='web:spencer' AND action='approval.allow';")));
    }

    [Fact(DisplayName = "Web view: a viewer reads but changes nothing and does not see the audit log; signing out ends the session")]
    public async Task ViewerAndLogout()
    {
        var (viewer, vcsrf) = await SignIn("viewer");
        using var _ = viewer;
        var card = Card();
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/approvals")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.SendAsync(Post($"/api/approvals/{card.Id}", new { decision = "allow", hash = card.ActionHash }, vcsrf, Origin))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/audit")).StatusCode);
        Assert.Equal("pending", new ApprovalStore(_db, _r.Clock, Redactor.Shared).Get(card.Id)!.Status);

        var (owner, csrf) = await SignIn();
        using var __ = owner;
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.SendAsync(Post("/api/logout", new { }, csrf, Origin))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/bots")).StatusCode);
    }

    [Fact(DisplayName = "Web view: text from bots is returned as escaped JSON, and rooms and notices work through the API")]
    public async Task EscapingAndRooms()
    {
        var (http, csrf) = await SignIn();
        using var _ = http;
        Card("Builder wants <img src=x onerror=alert(1)> and </script><script>alert(2)</script>");
        var r = await http.GetAsync("/api/approvals");
        Assert.Equal("application/json", r.Content.Headers.ContentType!.MediaType);
        var text = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<img", text);
        Assert.DoesNotContain("</script>", text);
        Assert.Contains("\\u003Cimg", text);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(Post("/api/rooms/core", new { text = "@Scout <b>status?</b>" }, csrf, Origin))).StatusCode);
        var thread = await http.GetFromJsonAsync<JsonObject>("/api/rooms/core");
        Assert.Equal("web:spencer", thread!["messages"]![0]!["author"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/rooms/nowhere")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/bots")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/routines")).StatusCode);
    }

    [Fact(DisplayName = "Local mode: the web view binds only to loopback, and oma-bot web open gives a one-time link")]
    public async Task LocalMode()
    {
        var c = new RosterConfig { WorkspaceRoots = ["/srv/work"] };
        c.Api.BindAddress = "0.0.0.0";
        Assert.Throws<InvalidDataException>(c.Validate);
        var env = new CliEnv { Paths = _r.Paths, AppDir = _r.AppDir, Clock = _r.Clock, Out = new StringWriter(), Err = new StringWriter() };
        Assert.Equal(0, await new CliApp(env).RunAsync(["web", "open"]));
        var link = env.Out.ToString()!.Split('\n').Single(l => l.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
        var code = link[(link.IndexOf("code=", StringComparison.Ordinal) + 5)..].Trim();
        using var http = Client();
        Assert.Equal(HttpStatusCode.Redirect, (await http.GetAsync("/login?code=" + code)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/api/session")).StatusCode);
        Assert.Equal(0, Convert.ToInt32(_db.Scalar("SELECT COUNT(*) FROM audit WHERE detail LIKE '%olc_%' OR subject LIKE '%olc_%';")));
    }
}
