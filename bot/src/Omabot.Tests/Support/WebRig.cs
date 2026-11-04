// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Omabot.Cli;
using Omabot.Common;
using Omabot.Config;
using Omabot.Db;
using Omabot.Web;

namespace Omabot.Tests.Support;

/// <summary>
/// A web-single roster on loopback for tests: the real web server, users.db,
/// the admin secret, and accounts with real time-based codes. Requests carry
/// the public Host and X-Forwarded-Proto the HTTPS front would add.
/// </summary>
public sealed class WebRig : IAsyncDisposable
{
    public const string Host = "roster.example.test";
    public const string Origin = "https://roster.example.test";
    public const string Password = "Tall pines 7 quiet river";

    public const string Team = """
        version: 1
        models:
          flash-next:
            upstream_model: Qwen3.8-Flash-Next
        bots:
          - name: Builder
            personality: You are Builder.
            model: flash-next
            tools: { allow: [FileRead, FileWrite] }
        """;

    public TempRoster R { get; } = new();
    public RosterDb Db { get; private set; } = null!;
    public AuditLog Audit { get; private set; } = null!;
    public WebAuth Auth { get; private set; } = null!;
    public WebAccounts Accounts { get; private set; } = null!;
    public byte[] Key { get; private set; } = null!;
    public int Port { get; private set; }
    public bool FrontOk { get; set; } = true;
    public Dictionary<string, byte[]> Seeds { get; } = new(StringComparer.Ordinal);
    private WebApplication? _app;

    public static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public static void WebSingle(RosterConfig c, int port)
    {
        c.Mode = "web-single";
        c.Web.PublicOrigin = Origin;
        c.Web.BehindHttpsFront = true;
        c.Api.Port = port;
    }

    /// <summary>A user roster's team: one bot and one room named after the user.</summary>
    public static string UserTeam(string bot, string room) => $$"""
        version: 1
        models:
          flash-next:
            upstream_model: Qwen3.8-Flash-Next
        rooms:
          - { name: {{room}}, members: [{{bot}}], responder: mentions }
        bots:
          - name: {{bot}}
            personality: You are {{bot}}.
            model: flash-next
            tools: { allow: [FileRead, FileWrite] }
        """;

    public static void WriteSignoff(RosterPaths p)
    {
        var file = Path.Combine(p.Web, "release-signoff.md");
        Common.SecureFile.EnsureDirNoClobber(p.Web);
        Common.SecureFile.WriteAllTextAtomic(file, "security_gate: passed\noutside_review: done\nsigned_by: Spencer\ndate: 2026-11-04\n");
    }

    /// <param name="multi">web-multi: user rosters alice (bot Alpha) and bob (bot Bravo), carol a viewer in alice's, spencer with no roster.</param>
    public static async Task<WebRig> StartAsync(Action<RosterConfig>? configure = null, bool multi = false)
    {
        var rig = new WebRig();
        var env = new CliEnv { Paths = rig.R.Paths, AppDir = rig.R.AppDir, Clock = rig.R.Clock, Out = new StringWriter(), Err = new StringWriter() };
        if (await new CliApp(env).RunAsync(["team", "apply", rig.R.TeamFile(Team)]) != 0) throw new InvalidOperationException(env.Err.ToString());
        rig.Db = rig.R.Db();
        rig.Audit = rig.R.Audit(rig.Db);
        rig.Port = FreePort();
        WebSingle(rig.R.Config, rig.Port);
        if (multi)
        {
            rig.R.Config.Mode = "web-multi";
            WriteSignoff(rig.R.Paths);
        }
        configure?.Invoke(rig.R.Config);
        WebSecrets.Create(rig.R.Paths.WebAdminSecret);
        rig.Key = WebSecrets.SeedKey(rig.R.Paths.WebAdminSecret);
        var users = new UsersDb(rig.R.Paths.UsersDb);
        rig.Auth = new WebAuth(users, rig.R.Clock, TimeSpan.FromHours(rig.R.Config.Web.SessionHours), TimeSpan.FromMinutes(rig.R.Config.Web.IdleMinutes));
        rig.Accounts = new WebAccounts(users, rig.R.Clock, rig.Auth, rig.R.Config, rig.Audit, iterations: 1_000);
        rig.AddAccount("spencer", "owner");
        if (multi)
        {
            foreach (var (user, bot) in new[] { ("alice", "Alpha"), ("bob", "Bravo") })
            {
                var home = Tenants.Ensure(users, rig.R.Config, rig.R.Paths, user, rig.R.Clock);
                var tenv = new CliEnv { Paths = home.Paths, AppDir = rig.R.AppDir, Clock = rig.R.Clock, Out = new StringWriter(), Err = new StringWriter() };
                if (await new CliApp(tenv).RunAsync(["team", "apply", rig.R.TeamFile(UserTeam(bot, bot.ToLowerInvariant() + "-room"))]) != 0) throw new InvalidOperationException(tenv.Err.ToString());
                rig.AddAccount(user, "owner", home: user);
            }
            rig.AddAccount("carol", "viewer", home: "alice");
        }
        var notices = new Notices(rig.Db, rig.R.Clock, Common.Redactor.Shared);
        var deps = new WebDeps
        {
            Db = rig.Db, Config = rig.R.Config, Paths = rig.R.Paths, Clock = rig.R.Clock, Audit = rig.Audit, Notices = notices, Auth = rig.Auth,
            Options = WebMode.Options(rig.R.Config), Accounts = rig.Accounts, SeedKey = () => rig.Key, FrontVerified = () => rig.FrontOk,
            HealthNonce = WebMode.NonceChecker(rig.R.Paths, new string('a', 32)),
            TenantFor = multi ? TenantWeb.Resolver(rig.R.Paths, rig.R.Clock, rig.Auth, WebMode.Options(rig.R.Config), rig.Accounts, users) : null,
        };
        rig._app = WebServer.Build(deps, IPAddress.Loopback, rig.Port);
        await rig._app.StartAsync();
        return rig;
    }

    /// <summary>Adds and confirms an account; its seed is kept for codes.</summary>
    public void AddAccount(string name, string role, string password = Password, string? home = null)
    {
        var (ok, message, uri) = Accounts.Add(name, role, password, Key, home);
        if (!ok) throw new InvalidOperationException(message);
        Seeds[name] = SeedOf(uri!);
        var c = Accounts.Confirm(name, Code(name), Key);
        if (!c.Ok) throw new InvalidOperationException(c.Message);
    }

    public static byte[] SeedOf(string uri)
    {
        var secret = uri.Split('?')[1].Split('&').First(p => p.StartsWith("secret=", StringComparison.Ordinal))[7..];
        return Totp.FromBase32(secret);
    }

    public string Code(string name) => Totp.Code(Seeds[name], Totp.StepAt(R.Clock.UtcNow));

    public HttpClient Http() => new(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{Port}") };

    public HttpRequestMessage Req(HttpMethod method, string path, object? body = null, string? cookie = null, string? csrf = null, string? origin = Origin, string? ip = null, bool https = true, string host = Host)
    {
        var m = new HttpRequestMessage(method, path);
        m.Headers.Host = host;
        if (https) m.Headers.Add("X-Forwarded-Proto", "https");
        if (ip is not null) m.Headers.Add("X-Forwarded-For", ip);
        if (method != HttpMethod.Get && origin is not null) m.Headers.Add("Origin", origin);
        if (cookie is not null) m.Headers.Add("Cookie", $"{WebMode.SecureCookie}={cookie}");
        if (csrf is not null) m.Headers.Add("X-CSRF-Token", csrf);
        if (body is not null) m.Content = JsonContent.Create(body);
        return m;
    }

    /// <summary>One sign-in. Without a code it moves the clock to a fresh 30 second step and uses that step's code.</summary>
    public async Task<(HttpResponseMessage Res, string? Cookie, string? SetCookie)> Login(HttpClient http, string name, string password = Password, string? code = null, string? ip = null, string? cookie = null, string? origin = Origin)
    {
        if (code is null && Seeds.ContainsKey(name))
        {
            R.Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
            code = Code(name);
        }
        var res = await http.SendAsync(Req(HttpMethod.Post, "/login", new { name, password, code = code ?? "000000" }, cookie: cookie, ip: ip, origin: origin));
        var set = res.Headers.TryGetValues("Set-Cookie", out var v) ? v.FirstOrDefault(c => c.StartsWith(WebMode.SecureCookie + "=", StringComparison.Ordinal)) : null;
        return (res, set?.Split(';')[0][(WebMode.SecureCookie.Length + 1)..], set);
    }

    public async Task<string> Csrf(HttpClient http, string cookie)
    {
        var res = await http.SendAsync(Req(HttpMethod.Get, "/api/session", cookie: cookie));
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonObject>())!["csrf"]!.GetValue<string>();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
        R.Dispose();
    }
}

/// <summary>
/// A stand-in for the HTTPS front: Kestrel with TLS on loopback, a leaf
/// certificate for <see cref="WebRig.Host"/> from a test CA, forwarding to a
/// roster's web port the way the Caddy recipe does.
/// </summary>
public sealed class TestFront : IAsyncDisposable
{
    public static readonly X509Certificate2 TrustedCa = NewCa("CN=oma-bot test CA");
    private readonly WebApplication _app;
    public int Port { get; }

    private TestFront(WebApplication app, int port) { _app = app; Port = port; }

    public static X509Certificate2 NewCa(string subject)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        using var ca = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(ca.Export(X509ContentType.Pfx), null);
    }

    public static X509Certificate2 Leaf(X509Certificate2 ca, string host)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        req.CertificateExtensions.Add(san.Build());
        using var signed = req.Create(ca, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(7), RandomNumberGenerator.GetBytes(8));
        using var withKey = signed.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    public static async Task<TestFront> StartAsync(int rosterPort, X509Certificate2? ca = null, bool setProto = true, string host = WebRig.Host)
    {
        var leaf = Leaf(ca ?? TrustedCa, host);
        var port = WebRig.FreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port, o => o.UseHttps(leaf)));
        var app = builder.Build();
        var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        app.Run(async ctx =>
        {
            using var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), $"http://127.0.0.1:{rosterPort}{ctx.Request.Path}{ctx.Request.QueryString}");
            req.Headers.Host = ctx.Request.Host.Value;
            if (setProto) req.Headers.Add("X-Forwarded-Proto", "https");
            req.Headers.Add("X-Forwarded-For", ctx.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1");
            using var res = await http.SendAsync(req);
            ctx.Response.StatusCode = (int)res.StatusCode;
            await ctx.Response.WriteAsync(await res.Content.ReadAsStringAsync());
        });
        await app.StartAsync();
        return new TestFront(app, port);
    }

    /// <summary>A client handler that reaches this front for any host name and trusts only <paramref name="trusted"/>.</summary>
    public HttpMessageHandler Handler(X509Certificate2? trusted = null) => new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ConnectCallback = async (_, ct) =>
        {
            var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await s.ConnectAsync(IPAddress.Loopback, Port, ct);
            return new NetworkStream(s, ownsSocket: true);
        },
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, cert, _, errors) =>
            {
                if (cert is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != 0) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(trusted ?? TrustedCa);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(X509CertificateLoader.LoadCertificate(cert.GetRawCertData()));
            },
        },
    };

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
