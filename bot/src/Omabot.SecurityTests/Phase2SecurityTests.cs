// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using Omabot.Cli;
using Omabot.Common;
using Omabot.Config;
using Omabot.Containers;
using Omabot.Doctor;
using Omabot.Gateway;
using Omabot.Render;
using Omabot.Secrets;
using Omabot.Tests.Support;

namespace Omabot.SecurityTests;

public sealed class Phase2SecurityTests : IDisposable
{
    private readonly TempRoster _r = new();
    public void Dispose() => _r.Dispose();

    private const string Canary = "canary-7f3a9c2e5b81d4f6a0c3e9b7";

    private (CliApp App, StringWriter Out, StringWriter Err) Cli(string? stdin = null, HttpClient? http = null)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var env = new CliEnv { Paths = _r.Paths, AppDir = _r.AppDir, Clock = _r.Clock, Out = o, Err = e, ReadStdin = () => stdin };
        return (http is null ? new CliApp(env) : new CliApp(env) { UpstreamHttp = http }, o, e);
    }

    [Fact(DisplayName = "secrets set takes the value from stdin, stores it 0600 in a 0700 bot folder, and prints no part of it")]
    public async Task SetFromStdin()
    {
        await Applied.DefaultPlusTwoAsync(_r);
        var (app, o, e) = Cli(Canary + "\n");
        Assert.Equal(0, await app.RunAsync(["secrets", "set", "github", "--bot", "Scout"]));
        var file = Path.Combine(_r.Paths.Bot("scout").SecretsDir, "github");
        Assert.Equal(Canary + "\n", File.ReadAllText(file));
        Assert.True(SecureFile.IsPrivate(file));
        Assert.Equal(SecureFile.PrivateDir, SecureFile.ModeOf(_r.Paths.Bot("scout").SecretsDir));
        Assert.DoesNotContain(Canary, o.ToString() + e.ToString());
        Assert.Contains("restart scout", o.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(Redactor.Shared.ContainsSecret("x " + Canary));
        foreach (var f in Directory.EnumerateFiles(_r.Paths.Logs)) Assert.DoesNotContain(Canary, File.ReadAllText(f));
        var (list, lo, _) = Cli();
        Assert.Equal(0, await list.RunAsync(["secrets", "list", "--bot", "Scout"]));
        Assert.Contains("github", lo.ToString());
        Assert.Contains("0600", lo.ToString());
        Assert.DoesNotContain(Canary, lo.ToString());
    }

    [Fact(DisplayName = "secrets set refuses a value on the command line")]
    public async Task NoValueArg()
    {
        await Applied.DefaultPlusTwoAsync(_r);
        var (app, o, e) = Cli();
        Assert.Equal(Exit.Invalid, await app.RunAsync(["secrets", "set", "github", "--bot", "Scout", "--value", Canary]));
        Assert.False(File.Exists(Path.Combine(_r.Paths.Bot("scout").SecretsDir, "github")));
        Assert.DoesNotContain(Canary, o.ToString() + e.ToString());
    }

    [Theory(DisplayName = "Startup secrets guard refuses empty, short and placeholder values without echoing them")]
    [InlineData("")]
    [InlineData("   \n")]
    [InlineData("changeme")]
    [InlineData("your-key-goes-here-please")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxx")]
    [InlineData("sk-example-key-0000")]
    public async Task Guard(string value)
    {
        Assert.NotNull(SecretsGuard.Problem(value));
        var (app, o, e) = Cli(value);
        Assert.Equal(Exit.Failed, await app.RunAsync(["secrets", "set", "inference"]));
        Assert.False(File.Exists(_r.Paths.InferenceKey));
        if (value.Trim().Length > 0) Assert.DoesNotContain(value.Trim(), o.ToString() + e.ToString());
    }

    [Fact(DisplayName = "A real looking key passes the guard; serve refuses to start with a placeholder key")]
    public async Task ServeGuard()
    {
        Assert.Null(SecretsGuard.Problem("sk-live-9f8e7d6c5b4a39281706f5e4d3c2b1a0"));
        Directory.CreateDirectory(_r.Paths.Secrets);
        SecureFile.WriteAllTextAtomic(_r.Paths.InferenceKey, "changeme\n");
        var (app, _, e) = Cli();
        Assert.Equal(Exit.Failed, await app.RunAsync(["serve", "--once"]));
        Assert.Contains("will not start", e.ToString());
        Assert.DoesNotContain("changeme", e.ToString());
    }

    [Fact(DisplayName = "Roster secret names are limited, and key files must stay under the secrets folder")]
    public void RosterNames()
    {
        var store = new SecretStore(_r.Paths, _r.Config, Redactor.Shared);
        Assert.Equal(_r.Paths.InferenceKey, store.PathFor("inference", null));
        Assert.Throws<SecretException>(() => store.PathFor("anything", null));
        Assert.Throws<SecretException>(() => store.PathFor("../etc", "scout"));
        var bad = new RosterConfig { WorkspaceRoots = ["/tmp"] };
        bad.Gateway.UpstreamKeyFile = "/etc/passwd";
        Assert.Throws<SecretException>(() => new SecretStore(_r.Paths, bad, Redactor.Shared).PathFor("inference", null));
    }

    [Fact(DisplayName = "SEC-08 (spec) team file secrets are copied only into that bot's folder, mounted read only, never into env")]
    public async Task TeamSecretsIsolated()
    {
        var src = Path.Combine(_r.Root, "gh.txt");
        File.WriteAllText(src, Canary);
        var yaml = TempRoster.TwoBots.Replace("""
              - name: Builder
            """, $"""
                secrets:
                  GITHUB_TOKEN: {"{"} file: {src} {"}"}
              - name: Builder
            """);
        var db = await Applied.DefaultPlusTwoAsync(_r);
        var (apply, _, ae) = Cli();
        Assert.True(await apply.RunAsync(["team", "apply", _r.TeamFile(yaml)]) == 0, ae.ToString());
        var bots = new Omabot.Db.BotStore(db, _r.Clock);
        var scout = bots.Get("Scout");
        var builder = bots.Get("Builder");
        Assert.Empty(BotSecrets.Materialize(scout.Spec, _r.Paths, Redactor.Shared));
        Assert.Empty(BotSecrets.Materialize(builder.Spec, _r.Paths, Redactor.Shared));
        Assert.Equal(Canary + "\n", File.ReadAllText(Path.Combine(_r.Paths.Bot("scout").SecretsDir, "GITHUB_TOKEN")));
        Assert.False(File.Exists(Path.Combine(_r.Paths.Bot("builder").SecretsDir, "GITHUB_TOKEN")));
        var renderer = new BotRenderer(_r.Paths, _r.Config);
        var spec = ContainerSpec.For(scout.Spec, scout.Port, "omb_t" + new string('q', 30), _r.Paths, _r.Config, renderer, _r.AppDir, false);
        var m = Assert.Single(spec.Mounts, x => x.Target == ContainerSpec.SecretsMount);
        Assert.True(m.ReadOnly);
        Assert.Equal(_r.Paths.Bot("scout").SecretsDir, m.Source);
        Assert.DoesNotContain(spec.Env, kv => kv.Value.Contains(Canary, StringComparison.Ordinal));
        var bspec = ContainerSpec.For(builder.Spec, builder.Port, "omb_t" + new string('q', 30), _r.Paths, _r.Config, renderer, _r.AppDir, false);
        Assert.DoesNotContain(bspec.Mounts, x => x.Source == _r.Paths.Bot("scout").SecretsDir);
    }

    [Fact(DisplayName = "Dropping a secret from the team file removes it; secrets set by hand stay")]
    public void ManifestKeepsHandSecrets()
    {
        var spec = new Omabot.Team.BotSpec { Name = "Scout", Slug = "scout" };
        spec.Secrets["a"] = "env:OMABOT_TEST_A";
        Assert.Empty(BotSecrets.Materialize(spec, _r.Paths, Redactor.Shared, n => n == "OMABOT_TEST_A" ? Canary : null));
        new SecretStore(_r.Paths, _r.Config, Redactor.Shared).Set("hand", "hand-9f8e7d6c5b4a3928", "scout");
        spec.Secrets.Clear();
        BotSecrets.Materialize(spec, _r.Paths, Redactor.Shared);
        var dir = _r.Paths.Bot("scout").SecretsDir;
        Assert.False(File.Exists(Path.Combine(dir, "a")));
        Assert.True(File.Exists(Path.Combine(dir, "hand")));
    }

    [Fact(DisplayName = "Missing env or file secret is reported by name only and blocks nothing else")]
    public void MissingSecret()
    {
        var spec = new Omabot.Team.BotSpec { Name = "Scout", Slug = "scout" };
        spec.Secrets["x"] = "env:OMABOT_NOT_SET_ANYWHERE";
        var p = Assert.Single(BotSecrets.Materialize(spec, _r.Paths, Redactor.Shared, _ => null));
        Assert.Contains("OMABOT_NOT_SET_ANYWHERE", p);
    }

    [Fact(DisplayName = "SEC-05 (unit) a stored secret that reaches a log is caught by doctor without printing it")]
    public async Task DoctorCatchesStoredSecret()
    {
        await Applied.DefaultPlusTwoAsync(_r);
        new SecretStore(_r.Paths, _r.Config, Redactor.Shared).Set("db", Canary, "builder");
        File.AppendAllText(Path.Combine(_r.Paths.Logs, "events-builder.log"), "leak " + Canary + "\n");
        var (app, o, e) = Cli();
        var code = await app.RunAsync(["doctor", "--json"]);
        Assert.NotEqual(0, code);
        Assert.DoesNotContain(Canary, o.ToString() + e.ToString());
    }

    [Fact(DisplayName = "SEC-12 exit check: after gateway traffic and an upstream error, the real key is in no log")]
    public async Task KeyNotInLogs()
    {
        const string realKey = "sk-realinferencekey0123456789abcdefXYZ";
        Directory.CreateDirectory(_r.Paths.Secrets);
        SecureFile.WriteAllTextAtomic(_r.Paths.InferenceKey, realKey);
        new SecretStore(_r.Paths, _r.Config, Redactor.Shared).LoadAllIntoRedactor();
        var h = new StubHandler((_, _) => throw new HttpRequestException("connect failed with " + realKey));
        var gw = new PassThroughGateway(() => new Dictionary<string, string> { [SecureFile.Sha256Hex("omb_goodtokengoodtokengood")] = "scout" },
            () => new Dictionary<string, ModelRoute> { ["flash-next"] = new("flash-next", "Q", "", "", "", []) },
            _ => realKey, new HttpClient(h), _r.Clock, false, TimeSpan.Zero)
        { Backends = new BackendRegistry(_r.Config, _r.Clock) };
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/v1/chat/completions";
        ctx.Request.Headers.Authorization = "Bearer omb_goodtokengoodtokengood";
        ctx.Request.Body = new MemoryStream("""{"model":"flash-next","messages":[]}"""u8.ToArray());
        ctx.Response.Body = new MemoryStream();
        await gw.HandleAsync(ctx);
        Assert.Equal(503, ctx.Response.StatusCode);
        ctx.Response.Body.Position = 0;
        Assert.DoesNotContain(realKey, new StreamReader(ctx.Response.Body).ReadToEnd());
        var db = _r.Db();
        _r.Audit(db).Write("roster", "test", "x", "upstream said " + realKey);
        foreach (var f in Directory.EnumerateFiles(_r.Paths.Logs, "*", SearchOption.AllDirectories)) Assert.DoesNotContain(realKey, File.ReadAllText(f));
    }

    [Fact(DisplayName = "gateway bench measures r1, f(S), pp and L against a stub server and --save fills the node class")]
    public async Task Bench()
    {
        Directory.CreateDirectory(_r.Paths.Secrets);
        SecureFile.WriteAllTextAtomic(_r.Paths.InferenceKey, "sk-live-9f8e7d6c5b4a39281706f5e4d3c2b1a0");
        var h = new StubHandler(async (req, body) =>
        {
            if (req.RequestUri!.AbsolutePath == "/v1/status") return StubHandler.Json("""{"concurrency":{"serving":2}}""");
            await Task.Delay(5);
            return StubHandler.ChatStream(8);
        });
        var (app, o, e) = Cli(http: new HttpClient(h));
        Assert.True(await app.RunAsync(["gateway", "bench", "--model", "Qwen3.8-Flash-Next", "--levels", "1,2", "--rounds", "1", "--save"]) == 0, e.ToString());
        var cfg = RosterConfig.Load(_r.Paths);
        var nc = Assert.Single(cfg.Gateway.NodeClasses);
        Assert.Equal(2, nc.MeasuredSlots);
        Assert.True(nc.TokensPerSecond > 0);
        Assert.Equal(1.0, nc.SpeedFactors["1"]);
        Assert.Equal("main", cfg.Gateway.UpstreamNodeClass);
        Assert.Contains("f(S)", o.ToString());
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_r.Paths.Home, "reports")));
        Assert.All(h.Seen.Where(s => s.Url.EndsWith("/chat/completions")), s => Assert.Equal("Qwen3.8-Flash-Next", JsonNode.Parse(s.Body)!["model"]!.GetValue<string>()));
        Assert.DoesNotContain("sk-live", o.ToString());
    }

    [Fact(DisplayName = "gateway bench refuses a plain http upstream off loopback (B1)")]
    public async Task BenchPolicy()
    {
        _r.Config.Gateway.Upstream = "http://10.0.0.9:7474/v1";
        _r.Config.Save(_r.Paths);
        var (app, _, e) = Cli(http: new HttpClient(new StubHandler((_, _) => Task.FromResult(StubHandler.Json("{}")))));
        Assert.Equal(Exit.Invalid, await app.RunAsync(["gateway", "bench", "--model", "Q"]));
    }

    [Fact(DisplayName = "secrets rotate-bot replaces the bot gateway token and prints no token")]
    public async Task RotateBot()
    {
        await Applied.DefaultPlusTwoAsync(_r);
        var old = new BotTokens(_r.Paths, Redactor.Shared).Ensure("scout");
        var (app, o, e) = Cli();
        Assert.Equal(0, await app.RunAsync(["secrets", "rotate-bot", "Scout"]));
        var now = File.ReadAllText(_r.Paths.Bot("scout").TokenFile).Trim();
        Assert.NotEqual(old, now);
        Assert.DoesNotContain(now, o.ToString() + e.ToString());
        Assert.DoesNotContain(SecureFile.Sha256Hex(old), new BotTokens(_r.Paths, Redactor.Shared).LoadHashes().Keys);
    }

    [Fact(Skip = "LOAD-06 / task C1 live: needs the main box (RTX 3090) through the relay; run oma-bot gateway bench --save on the roster host")]
    public void Load06Live() { }
}
