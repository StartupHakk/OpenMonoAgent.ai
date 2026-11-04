// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Omabot.Common;
using Omabot.Containers;
using Omabot.Db;
using Omabot.Doctor;
using Omabot.Gateway;
using Omabot.Render;
using Omabot.Supervision;
using Omabot.Tests.Support;

namespace Omabot.SecurityTests;

public sealed class Phase1SecurityTests : IDisposable
{
    private readonly TempRoster _r = new();
    public void Dispose() => _r.Dispose();

    private const string RealKey = "sk-realinferencekey0123456789abcdefXYZ";

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, string Body)> Seen { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request, body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"x","choices":[{"message":{"role":"assistant","content":"hi"}}]}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static PassThroughGateway Gateway(CaptureHandler h, string tokenHash, string slug, IClock clock) => new(
        () => new Dictionary<string, string> { [tokenHash] = slug },
        () => new Dictionary<string, ModelRoute>(StringComparer.OrdinalIgnoreCase)
        {
            ["flash-next"] = new("flash-next", "Qwen3.8-Flash-Next", "relay", "https://relay.example.test/v1", "/k", ["class=main-3090"]),
        },
        _ => RealKey,
        new HttpClient(h),
        clock,
        false,
        TimeSpan.FromSeconds(60));

    private static DefaultHttpContext Ctx(string method, string path, string? token, string? body = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (token is not null) ctx.Request.Headers.Authorization = "Bearer " + token;
        if (body is not null)
        {
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            ctx.Request.ContentType = "application/json";
        }
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ResponseText(DefaultHttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        return new StreamReader(ctx.Response.Body).ReadToEnd();
    }

    [Theory(DisplayName = "UNIT-GW-04 / SEC-02 unknown, missing or short token gets 401 and nothing goes upstream")]
    [InlineData(null)]
    [InlineData("omb_wrongtokenwrongtokenwrong")]
    [InlineData("short")]
    public async Task Gw04(string? token)
    {
        var h = new CaptureHandler();
        var gw = Gateway(h, SecureFile.Sha256Hex("omb_goodtokengoodtokengood"), "scout", _r.Clock);
        var ctx = Ctx("POST", "/v1/chat/completions", token, """{"model":"flash-next","messages":[]}""");
        await gw.HandleAsync(ctx);
        Assert.Equal(401, ctx.Response.StatusCode);
        Assert.Empty(h.Seen);
    }

    [Fact(DisplayName = "SEC-12 gateway swaps the model, adds the real key upstream, and never returns it to the bot")]
    public async Task Sec12Gateway()
    {
        var h = new CaptureHandler();
        const string botToken = "omb_goodtokengoodtokengood";
        var gw = Gateway(h, SecureFile.Sha256Hex(botToken), "scout", _r.Clock);
        var ctx = Ctx("POST", "/v1/chat/completions", botToken, """{"model":"flash-next","messages":[{"role":"user","content":"hi"}]}""");
        await gw.HandleAsync(ctx);
        Assert.Equal(200, ctx.Response.StatusCode);
        var (req, body) = Assert.Single(h.Seen);
        Assert.Equal("Qwen3.8-Flash-Next", JsonNode.Parse(body)!["model"]!.GetValue<string>());
        Assert.Equal("Bearer " + RealKey, req.Headers.Authorization!.ToString());
        Assert.DoesNotContain(botToken, req.Headers.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(RealKey, ResponseText(ctx), StringComparison.Ordinal);
        foreach (var hdr in ctx.Response.Headers) Assert.DoesNotContain(RealKey, hdr.Value.ToString(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Gateway refuses an unknown model nickname without calling upstream")]
    public async Task UnknownModel()
    {
        var h = new CaptureHandler();
        const string botToken = "omb_goodtokengoodtokengood";
        var gw = Gateway(h, SecureFile.Sha256Hex(botToken), "scout", _r.Clock);
        var ctx = Ctx("POST", "/v1/chat/completions", botToken, """{"model":"gpt-cloud","messages":[]}""");
        await gw.HandleAsync(ctx);
        Assert.InRange(ctx.Response.StatusCode, 400, 499);
        Assert.Empty(h.Seen);
    }

    [Theory(DisplayName = "B1 plain http upstream off loopback is refused unless allowed")]
    [InlineData("http://10.0.0.5:7474/v1", false, false)]
    [InlineData("http://10.0.0.5:7474/v1", true, true)]
    [InlineData("https://relay.example.test/v1", false, true)]
    [InlineData("http://127.0.0.1:7000/v1", false, true)]
    [InlineData("ftp://x/v1", true, false)]
    public void B1(string endpoint, bool allowPlain, bool ok)
        => Assert.Equal(ok, UpstreamPolicy.Check(endpoint, allowPlain) is null);

    [Fact(DisplayName = "SEC-12 the real inference key is in no rendered file or container env")]
    public async Task Sec12Files()
    {
        Directory.CreateDirectory(_r.Paths.Secrets);
        SecureFile.WriteAllTextAtomic(_r.Paths.InferenceKey, RealKey);
        var db = await Applied.DefaultPlusTwoAsync(_r);
        var renderer = new BotRenderer(_r.Paths, _r.Config);
        foreach (var row in new BotStore(db, _r.Clock).All())
        {
            var spec = ContainerSpec.For(row.Spec, row.Port, "omb_t" + new string('q', 30), _r.Paths, _r.Config, renderer, _r.AppDir, false);
            Assert.DoesNotContain(spec.Env.Values, v => v.Contains(RealKey, StringComparison.Ordinal));
            Assert.DoesNotContain(spec.Mounts, m => m.Source.StartsWith(_r.Paths.Secrets, StringComparison.Ordinal) && !m.Source.StartsWith(_r.Paths.BotSecretsRoot, StringComparison.Ordinal));
        }
        foreach (var f in Directory.EnumerateFiles(_r.Paths.Bots, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(RealKey, File.ReadAllText(f), StringComparison.Ordinal);
        foreach (var f in Directory.EnumerateFiles(_r.Paths.Logs, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(RealKey, File.ReadAllText(f), StringComparison.Ordinal);
    }

    [Theory(DisplayName = "SEC-13 hook injection: shell metacharacters in a tool name run nothing")]
    [InlineData("Bash;touch PWNED")]
    [InlineData("$(touch PWNED)")]
    [InlineData("`touch PWNED`")]
    [InlineData("a|touch PWNED")]
    [InlineData("x' ; touch PWNED ; '")]
    public void Sec13(string evil)
    {
        var hook = Path.Combine(Applied.RepoRoot(), "bot", "hooks", "pre-tool.sh");
        var list = Path.Combine(_r.Root, "allowed.txt");
        File.WriteAllText(list, "Bash\n");
        var psi = new ProcessStartInfo("bash") { WorkingDirectory = _r.Root, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add(hook);
        psi.ArgumentList.Add(evil);
        psi.Environment["ROSTER_ALLOWED_TOOLS"] = list;
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        Assert.Equal(2, p.ExitCode);
        Assert.False(File.Exists(Path.Combine(_r.Root, "PWNED")));
        // And settings never pass tool input to a hook.
        var hooks = BotRenderer.RenderHooks().ToJsonString();
        Assert.DoesNotContain("tool_input", hooks, StringComparison.Ordinal);
        Assert.Contains("{{tool_name}}", hooks, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "SEC-22 container hardening: caps dropped, no-new-privileges, not privileged, not root")]
    public async Task Sec22()
    {
        var db = await Applied.DefaultPlusTwoAsync(_r);
        var renderer = new BotRenderer(_r.Paths, _r.Config);
        foreach (var row in new BotStore(db, _r.Clock).All())
        {
            var spec = ContainerSpec.For(row.Spec, row.Port, "omb_t" + new string('q', 30), _r.Paths, _r.Config, renderer, _r.AppDir, false);
            var json = spec.ToEngineJson();
            var host = json["HostConfig"]!;
            Assert.NotEqual(true, host["Privileged"]?.GetValue<bool>());
            Assert.Contains("ALL", host["CapDrop"]!.AsArray().Select(x => x!.GetValue<string>()));
            Assert.Contains(host["SecurityOpt"]!.AsArray(), x => x!.GetValue<string>().StartsWith("no-new-privileges", StringComparison.Ordinal));
            Assert.Null(host["CapAdd"]);
            Assert.NotEqual("host", host["NetworkMode"]?.GetValue<string>());
            Assert.NotEqual("host", host["PidMode"]?.GetValue<string>());
            Assert.False(spec.User.StartsWith('0'));
        }
    }

    [Fact(DisplayName = "SEC-16 docker run args and command lines carry no secret values")]
    public async Task Sec16()
    {
        var db = await Applied.DefaultPlusTwoAsync(_r);
        var renderer = new BotRenderer(_r.Paths, _r.Config);
        var row = new BotStore(db, _r.Clock).Get("Scout");
        const string token = "omb_secretbottoken0123456789abcdef";
        var spec = ContainerSpec.For(row.Spec, row.Port, token, _r.Paths, _r.Config, renderer, _r.AppDir, false);
        Assert.DoesNotContain(token, string.Join(" ", spec.ToDockerRunArgs()), StringComparison.Ordinal);
        Assert.DoesNotContain(token, string.Join(" ", spec.Command), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "SEC-16 live: ps on the agent box shows no secrets", Skip = "Needs running roster containers on a Docker host.")]
    public void Sec16Live() { }

    [Fact(DisplayName = "SEC-21 supervisor socket: 0600, bearer required, unsafe spec refused, foreign names refused")]
    public async Task Sec21Supervisor()
    {
        var sock = Path.Combine(_r.Root, "sv.sock");
        const string token = "supervisor-token-0123456789abcdef";
        var engine = new FakeContainerEngine();
        await using var app = SupervisorHost.Build(sock, token, new DirectSupervisor(engine, "oma-bot-net"));
        await app.StartAsync();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(sock));
        using var good = new SocketSupervisorClient(sock, token);
        using var bad = new SocketSupervisorClient(sock, "wrong-token-0123456789abcdefgh");
        Assert.True(await good.PingAsync());
        Assert.False(await bad.PingAsync());

        var db = await Applied.DefaultPlusTwoAsync(_r);
        var row = new BotStore(db, _r.Clock).Get("Scout");
        var spec = ContainerSpec.For(row.Spec, row.Port, "omb_t" + new string('q', 30), _r.Paths, _r.Config, new BotRenderer(_r.Paths, _r.Config), _r.AppDir, false);
        await Assert.ThrowsAnyAsync<Exception>(() => bad.EnsureRunningAsync(spec));
        Assert.Empty(engine.Created);
        var unsafeSpec = spec with { Mounts = [.. spec.Mounts, new Mount("/var/run/docker.sock", "/var/run/docker.sock", false)] };
        await Assert.ThrowsAnyAsync<Exception>(() => good.EnsureRunningAsync(unsafeSpec));
        Assert.Empty(engine.Created);
        var id = await good.EnsureRunningAsync(spec);
        Assert.False(string.IsNullOrEmpty(id));
        Assert.Single(engine.Created);
        await Assert.ThrowsAnyAsync<Exception>(() => good.StopAsync("openmono-agent"));
        Assert.Single(await good.ListAsync());
        await app.StopAsync();
    }

    [Fact(DisplayName = "SEC-21 bot hooks block reaching ACP, sockets and metadata through tool input")]
    public void Sec21Hooks()
    {
        var pre = BotRenderer.RenderHooks()["pre_tool_use"]!.AsArray();
        var blocked = pre.Select(h => h!["if"]?["input_contains"]?.GetValue<string>()).Where(x => x is not null).ToHashSet();
        foreach (var b in BotRenderer.BlockedInputs) Assert.Contains(b, blocked);
        foreach (var must in new[] { ":7475", "roster.sock", "supervisor.sock", "docker.sock", "host.docker.internal" }) Assert.Contains(must, blocked);
    }

    [Fact(DisplayName = "SEC-21 live: a bot cannot reach the roster API, another bot or the sockets", Skip = "Needs Docker and the openmono-agent image.")]
    public void Sec21Live() { }

    [Fact(DisplayName = "SEC-24 audit tampering breaks the hash chain and doctor reports it")]
    public async Task Sec24()
    {
        var db = await Applied.DefaultPlusTwoAsync(_r);
        var audit = _r.Audit(db);
        Assert.Null(audit.Verify());
        // Triggers refuse edits; someone with the file drops them first.
        Assert.ThrowsAny<Exception>(() => db.Exec("UPDATE audit SET detail = 'nothing happened' WHERE id = 2;"));
        foreach (var trig in db.Query("SELECT name FROM sqlite_master WHERE type='trigger' AND tbl_name='audit';", r => r.GetString(0)))
            db.Exec($"DROP TRIGGER {trig};");
        db.Exec("UPDATE audit SET detail = 'nothing happened' WHERE id = 2;");
        var broken = audit.Verify();
        Assert.NotNull(broken);
        var results = await new DoctorChecks(_r.Paths, _r.Clock).RunAsync(new DoctorOptions(_r.AppDir));
        Assert.Contains(results, r => r.Status == CheckStatus.Fail && r.Name.Contains("audit", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, DoctorChecks.ExitCode(results));
    }

    [Theory(DisplayName = "UNIT-MEM-05 redaction replaces each secret class with the redacted mark")]
    [InlineData("key sk-abcdefghijklmnopqrstuvwxyz012345")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("xoxb-1234567890-abcdefghij")]
    [InlineData("AKIAABCDEFGHIJKLMNOP")]
    [InlineData("Authorization: Bearer abcdefghijklmnop1234")]
    [InlineData("password=hunter2hunter2")]
    [InlineData("postgres://user:pa55word@db:5432/x")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIabc\n-----END RSA PRIVATE KEY-----")]
    public void Mem05(string text)
    {
        var red = new Redactor().Redact(text);
        Assert.NotEqual(text, red);
        Assert.Contains("\u00abredacted ", red, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Known secrets are redacted exactly, and plain text is left alone")]
    public void KnownSecret()
    {
        var r = new Redactor();
        r.AddKnownSecret("plain-but-secret-value");
        Assert.DoesNotContain("plain-but-secret-value", r.Redact("x plain-but-secret-value y"), StringComparison.Ordinal);
        Assert.Equal("Scout found 3 files in /workspace.", r.Redact("Scout found 3 files in /workspace."));
    }

    [Fact(DisplayName = "Doctor fails closed when a log holds a secret, and never prints it")]
    public async Task DoctorSecretScan()
    {
        await Applied.DefaultPlusTwoAsync(_r);
        Directory.CreateDirectory(_r.Paths.Logs);
        File.WriteAllText(Path.Combine(_r.Paths.Logs, "events-scout.log"), "oops token sk-leakedleakedleakedleaked0123\n");
        var results = await new DoctorChecks(_r.Paths, _r.Clock).RunAsync(new DoctorOptions(_r.AppDir));
        Assert.Equal(1, DoctorChecks.ExitCode(results));
        var text = DoctorChecks.Format(results, false) + DoctorChecks.ToJson(results);
        Assert.DoesNotContain("sk-leaked", text, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Roster secrets and database are private to the owner")]
    public async Task FileModes()
    {
        await Applied.DefaultPlusTwoAsync(_r);
        var tokens = new Secrets.BotTokens(_r.Paths, new Redactor());
        tokens.Ensure("scout");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(_r.Paths.Secrets));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_r.Paths.Db));
    }
}
