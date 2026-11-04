// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Omabot.Approvals;
using Omabot.Cli;
using Omabot.Common;
using Omabot.Config;
using Omabot.Tests.Support;
using Omabot.Web;

namespace Omabot.SecurityTests;

/// <summary>Phase 10: web-single behind the HTTPS front (plan 7.19, 7.20; SEC-15, SEC-17 to SEC-19, SEC-23).</summary>
public sealed class Phase10SecurityTests
{
    private static CliEnv Env(TempRoster r, string? stdin = null, Func<HttpMessageHandler?>? https = null) => new()
    {
        Paths = r.Paths, AppDir = r.AppDir, Clock = r.Clock, Out = new StringWriter(), Err = new StringWriter(),
        ReadStdin = () => stdin, HttpsHandler = https ?? (() => null),
    };

    private static ApprovalRow Card(WebRig rig, string? boundTo = null, TimeSpan? ttl = null)
        => new ApprovalStore(rig.Db, rig.R.Clock, Redactor.Shared).Create(new ApprovalRequest(ApprovalKinds.Permission, "builder", "Builder wants to run Bash: dotnet test",
            new JsonObject { ["command"] = "dotnet test" }, "bot", "builder", SessionId: "ses_w", PauseId: "perm_" + Guid.NewGuid().ToString("N")[..6],
            Ttl: ttl ?? TimeSpan.FromMinutes(10), Action: "dotnet test", BoundTo: boundTo));

    [Fact(DisplayName = "SEC-15 web: a cross-origin write is refused; changes need this page's Origin and the session's token; the session cookie is __Host-, Secure, HttpOnly, SameSite=Strict")]
    public async Task Sec15()
    {
        await using var rig = await WebRig.StartAsync();
        using var http = rig.Http();
        var (res, cookie, set) = await rig.Login(http, "spencer");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var flags = set!.ToLowerInvariant();
        Assert.StartsWith(WebMode.SecureCookie + "=", set);
        foreach (var f in new[] { "secure", "httponly", "samesite=strict", "path=/", "max-age=" }) Assert.Contains(f, flags);
        Assert.DoesNotContain("domain=", flags);
        var csrf = await rig.Csrf(http, cookie!);
        var notice = new Db.Notices(rig.Db, rig.R.Clock, Redactor.Shared);
        notice.Add("warn", null, "test notice", "t1");
        var id = notice.Unseen().Single().Id;
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(rig.Req(HttpMethod.Post, $"/api/notices/{id}/seen", new { }, cookie, csrf, origin: "https://evil.example"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(rig.Req(HttpMethod.Post, $"/api/notices/{id}/seen", new { }, cookie, csrf, origin: null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(rig.Req(HttpMethod.Post, $"/api/notices/{id}/seen", new { }, cookie, csrf: null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(rig.Req(HttpMethod.Post, $"/api/notices/{id}/seen", new { }, cookie, csrf: "x" + csrf[1..]))).StatusCode);
        Assert.Single(notice.Unseen());
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(rig.Req(HttpMethod.Post, $"/api/notices/{id}/seen", new { }, cookie, csrf))).StatusCode);
        Assert.Empty(notice.Unseen());
        // Sign-in from another site is refused too (login CSRF), and so is plain http past the front or another host name.
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Login(http, "spencer", origin: "https://evil.example")).Res.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(rig.Req(HttpMethod.Get, "/api/session", cookie: cookie, https: false))).StatusCode);
        Assert.Equal((HttpStatusCode)421, (await http.SendAsync(rig.Req(HttpMethod.Get, "/api/session", cookie: cookie, host: "127.0.0.1:" + rig.Port))).StatusCode);
        var page = await http.SendAsync(rig.Req(HttpMethod.Get, "/"));
        Assert.Equal("max-age=31536000", page.Headers.GetValues("Strict-Transport-Security").Single());
        Assert.Equal(WebServer.Csp, page.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact(DisplayName = "SEC-17 web mode refuses to start without HTTPS, a built login method, the admin secret and an owner")]
    public async Task Sec17Problems()
    {
        await using var rig = await WebRig.StartAsync();
        var c = rig.R.Config;
        var p = rig.R.Paths;
        Assert.Empty(WebMode.Problems(c, p));
        void Expect(Action<RosterConfig> change, string words)
        {
            var copy = c.Clone();
            change(copy);
            Assert.Contains(WebMode.Problems(copy, p), x => x.Contains(words, StringComparison.Ordinal));
        }
        Expect(x => x.Web.PublicOrigin = "http://roster.example.test", "https");
        Expect(x => x.Web.PublicOrigin = null, "not set");
        Expect(x => x.Web.PublicOrigin = "https://roster.example.test/app", "only the scheme and host");
        Expect(x => x.Web.PublicOrigin = "https://203.0.113.5", "DNS name");
        Expect(x => x.Web.PublicOrigin = "https://localhost", "DNS name");
        Expect(x => x.Web.BehindHttpsFront = false, "behind_https_front");
        Expect(x => x.Web.TlsCertFile = "/etc/ssl/roster.pem", "does not terminate TLS");
        Expect(x => x.Web.LoginMethod = "passkey", "passkey is not built yet");
        Expect(x => x.Mode = "web-multi", "outside review");

        var secret = p.WebAdminSecret;
        File.SetUnixFileMode(secret, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        Assert.Contains(WebMode.Problems(c, p), x => x.Contains("readable by others", StringComparison.Ordinal));
        SecureFile.WriteAllTextAtomic(secret, new string('a', 40));
        Assert.Contains(WebMode.Problems(c, p), x => x.Contains("too weak", StringComparison.Ordinal));
        File.Delete(secret);
        Assert.Contains(WebMode.Problems(c, p), x => x.Contains("does not exist", StringComparison.Ordinal));

        Assert.True(WebSecrets.Create(secret));
        Assert.Empty(WebMode.Problems(c, p));
        Assert.True(rig.Accounts.SetRole("spencer", "operator").Ok == false);
        rig.AddAccount("second", "owner");
        Assert.True(rig.Accounts.Disable("spencer").Ok);
        Assert.True(rig.Accounts.Disable("second").Ok == false);
        rig.R.Config.Mode = "web-single";
        var users = new UsersDb(p.UsersDb);
        users.Exec("UPDATE accounts SET state='pending';");
        Assert.Contains(WebMode.Problems(c, p), x => x.Contains("No active owner", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "SEC-17 serve and doctor: web mode with a missing admin secret refuses to start and doctor reports it")]
    public async Task Sec17Serve()
    {
        using var r = new TempRoster();
        WebRig.WebSingle(r.Config, WebRig.FreePort());
        r.Config.Save(r.Paths);
        var env = Env(r);
        Assert.Equal(Exit.Failed, await new CliApp(env).RunAsync(["serve", "--once"]));
        Assert.Contains("Web mode refuses to start", env.Err.ToString());
        Assert.Contains("web admin secret", env.Err.ToString());
        var doc = Env(r);
        Assert.Equal(1, await new CliApp(doc).RunAsync(["doctor"]));
        Assert.Contains("web admin secret", doc.Out.ToString());
    }

    [Fact(DisplayName = "SEC-17 web enable: refused without an owner, without https, without a reachable front, with an untrusted certificate or a front that drops the https mark; on with a valid front")]
    public async Task Sec17Enable()
    {
        using var r = new TempRoster();
        var setup = Env(r);
        Assert.Equal(0, await new CliApp(setup).RunAsync(["team", "apply", r.TeamFile(WebRig.Team)]));
        var port = WebRig.FreePort();
        r.Config.Api.Port = port;
        r.Config.Save(r.Paths);
        var db = r.Db();
        var deps = new WebDeps
        {
            Db = db, Config = r.Config, Paths = r.Paths, Clock = r.Clock, Audit = r.Audit(db), Notices = new Db.Notices(db, r.Clock, Redactor.Shared),
            Auth = new WebAuth(new UsersDb(r.Paths.UsersDb), r.Clock, TimeSpan.FromHours(12)), Options = WebServer.Local(r.Config),
            HealthNonce = WebMode.NonceChecker(r.Paths, WebMode.NewNonce()),
        };
        var local = WebServer.Build(deps, IPAddress.Loopback, port);
        await local.StartAsync();
        try
        {
            async Task<(int Code, string Err)> Enable(string origin, Func<HttpMessageHandler?> https)
            {
                var e = Env(r, https: https);
                var code = await new CliApp(e).RunAsync(["web", "enable", "--public-origin", origin]);
                return (code, e.Err.ToString() ?? "");
            }
            var noOwner = await Enable(WebRig.Origin, () => null);
            Assert.Equal(1, noOwner.Code);
            Assert.Contains("admin secret", noOwner.Err);
            Assert.Equal(0, await new CliApp(Env(r)).RunAsync(["web", "admin-secret"]));
            var add = Env(r, stdin: WebRig.Password + "\n");
            Assert.Equal(0, await new CliApp(add).RunAsync(["users", "add", "spencer", "--role", "owner"]));
            Assert.Contains("No active owner", (await Enable(WebRig.Origin, () => null)).Err);
            var enroll = Path.Combine(r.Paths.WebEnroll, "spencer.txt");
            var seed = WebRig.SeedOf(File.ReadAllLines(enroll)[1]);
            Assert.Equal(0, await new CliApp(Env(r, stdin: Totp.Code(seed, Totp.StepAt(r.Clock.UtcNow)))).RunAsync(["users", "confirm", "spencer"]));

            Assert.Contains("https", (await Enable("http://roster.example.test", () => null)).Err);
            await using (var nobody = await TestFront.StartAsync(port))
            {
                var unreachable = await Enable(WebRig.Origin, () => new SocketsHttpHandler { UseProxy = false, ConnectCallback = (_, _) => throw new HttpRequestException("connection refused") });
                Assert.Equal(1, unreachable.Code);
                Assert.Contains("cannot be reached", unreachable.Err);
            }
            await using (var rogue = await TestFront.StartAsync(port, ca: TestFront.NewCa("CN=rogue CA")))
            {
                var bad = await Enable(WebRig.Origin, () => rogue.Handler());
                Assert.Equal(1, bad.Code);
                Assert.Contains("certificate is not valid", bad.Err);
            }
            await using (var plain = await TestFront.StartAsync(port, setProto: false))
            {
                var noProto = await Enable(WebRig.Origin, () => plain.Handler());
                Assert.Equal(1, noProto.Code);
                Assert.Contains("X-Forwarded-Proto", noProto.Err);
            }
            Assert.Equal("local", RosterConfig.Load(r.Paths).Mode);
            Assert.False(File.Exists(r.Paths.WebCheckNonce));
            await using (var front = await TestFront.StartAsync(port))
            {
                var ok = await Enable(WebRig.Origin, () => front.Handler());
                Assert.True(ok.Code == 0, ok.Err);
            }
            var saved = RosterConfig.Load(r.Paths);
            Assert.Equal("web-single", saved.Mode);
            Assert.Equal(WebRig.Origin, saved.Web.PublicOrigin);
            Assert.True(saved.Web.BehindHttpsFront);
            Assert.Equal(0, await new CliApp(Env(r)).RunAsync(["web", "disable"]));
            Assert.Equal("local", RosterConfig.Load(r.Paths).Mode);
        }
        finally
        {
            await local.StopAsync();
            await local.DisposeAsync();
        }
    }

    [Fact(DisplayName = "SEC-18 password guessing: the account locks after five failures with growing backoff, the address locks too, every failure is audited without the password")]
    public async Task Sec18()
    {
        await using var rig = await WebRig.StartAsync(c => c.Web.AddressFailures = 8);
        using var http = rig.Http();
        for (var i = 0; i < 5; i++)
        {
            var bad = await rig.Login(http, "spencer", password: "Wrong guess number " + i, ip: "198.51.100." + (10 + i));
            Assert.Equal(HttpStatusCode.Unauthorized, bad.Res.StatusCode);
        }
        var locked = await rig.Login(http, "spencer", ip: "198.51.100.30");
        Assert.Equal(HttpStatusCode.Unauthorized, locked.Res.StatusCode);
        Assert.Null(locked.Cookie);
        Assert.Equal(WebAccounts.Refused, (await locked.Res.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.GetValue<string>());
        var unknown = await rig.Login(http, "nobody", password: "Whatever it is 123", ip: "198.51.100.31");
        Assert.Equal(WebAccounts.Refused, (await unknown.Res.Content.ReadFromJsonAsync<JsonObject>())!["error"]!.GetValue<string>());
        Assert.NotNull(rig.Accounts.Find("spencer")!.LockedUntil);

        rig.R.Clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(HttpStatusCode.OK, (await rig.Login(http, "spencer", ip: "198.51.100.32")).Res.StatusCode);
        // Backoff: after a success the count starts over, so the next lockout is 15 minutes again; a lockout right after one doubles.
        for (var i = 0; i < 5; i++) await rig.Login(http, "spencer", password: "Wrong again here " + i, ip: "198.51.100.40");
        rig.R.Clock.Advance(TimeSpan.FromMinutes(16));
        for (var i = 0; i < 5; i++) await rig.Login(http, "spencer", password: "And wrong again " + i, ip: "198.51.100.41");
        var second = rig.Accounts.Find("spencer")!;
        Assert.Equal(2, second.Lockouts);
        Assert.True(second.LockedUntil! - rig.R.Clock.UtcNow > TimeSpan.FromMinutes(29));
        rig.R.Clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Login(http, "spencer", ip: "198.51.100.42")).Res.StatusCode);
        rig.R.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(HttpStatusCode.OK, (await rig.Login(http, "spencer", ip: "198.51.100.43")).Res.StatusCode);

        // One address guessing many names is locked out, even for a right password; other addresses are not.
        rig.AddAccount("alex", "operator");
        for (var i = 0; i < 8; i++) await rig.Login(http, "guess" + i, password: "Password guess " + i, ip: "203.0.113.9");
        var blocked = await rig.Login(http, "alex", ip: "203.0.113.9");
        Assert.Equal((HttpStatusCode)429, blocked.Res.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.Login(http, "alex", ip: "203.0.113.10")).Res.StatusCode);

        var failures = rig.Db.Query("SELECT detail FROM audit WHERE action='web.login_failed';", x => x.GetString(0));
        Assert.True(failures.Count >= 23);
        Assert.Contains(failures, f => f.Contains("account locked until", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("address locked until", StringComparison.Ordinal));
        var all = string.Join("\n", rig.Db.Query("SELECT actor || ' ' || action || ' ' || subject || ' ' || detail FROM audit;", x => x.GetString(0)));
        Assert.DoesNotContain("Wrong guess", all);
        Assert.DoesNotContain(WebRig.Password, all);
    }

    [Fact(DisplayName = "SEC-18 codes: a missing, wrong or reused code is refused, and a code only works for its own account")]
    public async Task Sec18Codes()
    {
        await using var rig = await WebRig.StartAsync();
        rig.AddAccount("alex", "operator");
        using var http = rig.Http();
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Login(http, "spencer", code: "")).Res.StatusCode);
        rig.R.Clock.Advance(TimeSpan.FromSeconds(30));
        var code = rig.Code("spencer");
        var wrong = code == "123456" ? "654321" : "123456";
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Login(http, "spencer", code: wrong)).Res.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Login(http, "alex", code: code)).Res.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.Login(http, "spencer", code: code)).Res.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Login(http, "spencer", code: code)).Res.StatusCode);
    }

    [Fact(DisplayName = "SEC-19 sessions: a new id at every sign-in, idle and absolute expiry, sign out everywhere, and disabling or changing a role ends sessions")]
    public async Task Sec19()
    {
        await using var rig = await WebRig.StartAsync();
        using var http = rig.Http();
        async Task<HttpStatusCode> Session(string cookie) => (await http.SendAsync(rig.Req(HttpMethod.Get, "/api/session", cookie: cookie))).StatusCode;

        var planted = new string('c', 64);
        var first = await rig.Login(http, "spencer", cookie: planted);
        Assert.NotEqual(planted, first.Cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, await Session(planted));
        var second = await rig.Login(http, "spencer", cookie: first.Cookie);
        Assert.NotEqual(first.Cookie, second.Cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, await Session(first.Cookie!));
        Assert.Equal(HttpStatusCode.OK, await Session(second.Cookie!));
        Assert.DoesNotContain(second.Cookie!, File.ReadAllText(rig.R.Paths.UsersDb));

        rig.R.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(HttpStatusCode.Unauthorized, await Session(second.Cookie!));

        var longLived = (await rig.Login(http, "spencer")).Cookie!;
        for (var t = 0; t < 35; t++)
        {
            rig.R.Clock.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(HttpStatusCode.OK, await Session(longLived));
        }
        rig.R.Clock.Advance(TimeSpan.FromMinutes(21));
        Assert.Equal(HttpStatusCode.Unauthorized, await Session(longLived));

        var a = (await rig.Login(http, "spencer")).Cookie!;
        var b = (await rig.Login(http, "spencer")).Cookie!;
        var csrf = await rig.Csrf(http, a);
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(rig.Req(HttpMethod.Post, "/api/logout-all", new { }, a, csrf))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, await Session(a));
        Assert.Equal(HttpStatusCode.Unauthorized, await Session(b));

        rig.AddAccount("alex", "operator");
        var alex = (await rig.Login(http, "alex")).Cookie!;
        Assert.True(rig.Accounts.SetRole("alex", "viewer").Ok);
        Assert.Equal(HttpStatusCode.Unauthorized, await Session(alex));
        alex = (await rig.Login(http, "alex")).Cookie!;
        var me = await (await http.SendAsync(rig.Req(HttpMethod.Get, "/api/session", cookie: alex))).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("viewer", me!["role"]!.GetValue<string>());
        Assert.True(rig.Accounts.Disable("alex").Ok);
        Assert.Equal(HttpStatusCode.Unauthorized, await Session(alex));
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Login(http, "alex")).Res.StatusCode);
    }

    [Fact(DisplayName = "SEC-19 front gate: until the HTTPS front checks out, sign-in and the API answer 503; /healthz answers only the roster's own value")]
    public async Task Sec19FrontGate()
    {
        await using var rig = await WebRig.StartAsync();
        using var http = rig.Http();
        var cookie = (await rig.Login(http, "spencer")).Cookie!;
        rig.FrontOk = false;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await http.SendAsync(rig.Req(HttpMethod.Get, "/api/session", cookie: cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await rig.Login(http, "spencer")).Res.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(rig.Req(HttpMethod.Get, "/healthz?n=" + new string('b', 32)))).StatusCode);
        var health = await http.SendAsync(rig.Req(HttpMethod.Get, "/healthz?n=" + new string('a', 32), host: "anything.example"));
        Assert.Equal("ok oma-bot https", await health.Content.ReadAsStringAsync());
        rig.FrontOk = true;
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(rig.Req(HttpMethod.Get, "/api/session", cookie: cookie))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.SendAsync(rig.Req(HttpMethod.Get, "/login?code=olc_" + new string('d', 64)))).StatusCode);
        var source = await http.SendAsync(rig.Req(HttpMethod.Get, "/source"));
        Assert.Equal(HttpStatusCode.Redirect, source.StatusCode);
        Assert.Equal(rig.R.Config.Web.SourceUrl, source.Headers.Location!.ToString());
    }

    [Fact(DisplayName = "SEC-23 web: an answer with another action's hash, a replayed answer, an expired card and a card bound to another person are refused")]
    public async Task Sec23()
    {
        await using var rig = await WebRig.StartAsync();
        rig.AddAccount("alex", "operator");
        using var http = rig.Http();
        var cookie = (await rig.Login(http, "spencer")).Cookie!;
        var csrf = await rig.Csrf(http, cookie);
        var store = new ApprovalStore(rig.Db, rig.R.Clock, Redactor.Shared);
        async Task<HttpStatusCode> Answer(string id, string hash, string c, string t) =>
            (await http.SendAsync(rig.Req(HttpMethod.Post, "/api/approvals/" + id, new { decision = "allow", hash }, c, t))).StatusCode;

        var card = Card(rig);
        var other = Card(rig);
        Assert.Equal(HttpStatusCode.Conflict, await Answer(card.Id, other.ActionHash, cookie, csrf));
        Assert.Equal("pending", store.Get(card.Id)!.Status);
        Assert.Equal(HttpStatusCode.OK, await Answer(card.Id, card.ActionHash, cookie, csrf));
        Assert.Equal(HttpStatusCode.Conflict, await Answer(card.Id, card.ActionHash, cookie, csrf));

        var late = Card(rig, ttl: TimeSpan.FromMinutes(1));
        rig.R.Clock.Advance(TimeSpan.FromMinutes(2));
        cookie = (await rig.Login(http, "spencer")).Cookie!;
        csrf = await rig.Csrf(http, cookie);
        Assert.Equal(HttpStatusCode.Conflict, await Answer(late.Id, late.ActionHash, cookie, csrf));

        var bound = Card(rig, boundTo: "person:spencer");
        var alex = (await rig.Login(http, "alex")).Cookie!;
        var alexCsrf = await rig.Csrf(http, alex);
        Assert.Equal(HttpStatusCode.Conflict, await Answer(bound.Id, bound.ActionHash, alex, alexCsrf));
        Assert.Equal("pending", store.Get(bound.Id)!.Status);
        Assert.Equal(HttpStatusCode.OK, await Answer(bound.Id, bound.ActionHash, cookie, csrf));
        var refused = rig.Db.Query("SELECT detail FROM audit WHERE action='approval.refused';", x => x.GetString(0));
        Assert.Contains(refused, d => d.Contains("different action", StringComparison.Ordinal));
        Assert.Contains(refused, d => d.Contains("already allowed", StringComparison.Ordinal));
        Assert.Contains(refused, d => d.Contains("expired", StringComparison.Ordinal));
        Assert.Contains(refused, d => d.Contains("bound to another person", StringComparison.Ordinal));
    }
}
