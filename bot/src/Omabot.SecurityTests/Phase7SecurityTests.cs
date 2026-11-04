// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Omabot.Cli;
using Omabot.Common;
using Omabot.Containers;
using Omabot.Db;
using Omabot.Desktops;
using Omabot.Egress;
using Omabot.McpShim;
using Omabot.Render;
using Omabot.Routines;
using Omabot.Supervision;
using Omabot.Tests.Support;

namespace Omabot.SecurityTests;

/// <summary>
/// Phase 7 threat tests: no route to the LAN, localhost, metadata or carrier
/// grade NAT from agents and desktops (SEC-06), DNS rebinding and redirects
/// (SEC-07), Facebook and X (SEC-11), and the fail-closed firewall check.
/// </summary>
public sealed class Phase7SecurityTests : IAsyncLifetime
{
    private const string Team = """
        version: 1
        models:
          flash-next:
            upstream_model: Qwen3.8-Flash-Next
        bots:
          - name: Builder
            personality: You are Builder.
            model: flash-next
            desktop: own
            web: { search: true, scrape: true, allow_hosts: [docs.example.org] }
            tools: { allow: [FileRead, WebSearch, WebFetch] }
          - name: Scout
            personality: You are Scout.
            model: flash-next
            web: { allow_hosts: [example.org] }
            tools: { allow: [FileRead] }
        """;

    private readonly TempRoster _r = new();
    private RosterDb _db = null!;
    private BotStore _bots = null!;

    public async Task InitializeAsync()
    {
        var (code, _, err) = await Cli("team", "apply", _r.TeamFile(Team));
        Assert.True(code == 0, err);
        _db = _r.Db();
        _bots = new BotStore(_db, _r.Clock);
    }

    public Task DisposeAsync()
    {
        _r.Dispose();
        return Task.CompletedTask;
    }

    private async Task<(int Code, string Out, string Err)> Cli(params string[] args)
    {
        var env = new CliEnv { Paths = _r.Paths, AppDir = _r.AppDir, Clock = _r.Clock, Out = new StringWriter(), Err = new StringWriter() };
        var code = await new CliApp(env).RunAsync(args);
        return (code, env.Out.ToString()!, env.Err.ToString()!);
    }

    private string LogPath => Path.Combine(_r.Root, "egress.log");

    private static readonly Dictionary<string, string[]> Inside = new(StringComparer.Ordinal)
    {
        ["router.example.org"] = ["192.168.1.1"],
        ["nas.example.org"] = ["10.0.0.5"],
        ["self.example.org"] = ["127.0.0.1"],
        ["meta.example.org"] = ["169.254.169.254"],
        ["cgnat.example.org"] = ["100.64.1.1"],
        ["v6local.example.org"] = ["fd00::5"],
        ["mapped.example.org"] = ["::ffff:192.168.1.1"],
        ["example.org"] = ["93.184.216.34"],
    };

    [Fact(DisplayName = "SEC-06: LAN, localhost, metadata and carrier grade NAT addresses are refused for agents and desktops, by address or by name, and logged")]
    public async Task Sec06()
    {
        var connector = new MapConnector([]);
        var resolver = new MapResolver((h, _) => Inside.TryGetValue(h, out var a) ? a : []);
        string[] names = ["router.example.org", "nas.example.org", "self.example.org", "meta.example.org", "cgnat.example.org", "v6local.example.org", "mapped.example.org"];
        await using var rig = new EgressRig(LogPath, _r.Clock, resolver, connector,
            ("builder", "agent", [.. names, "example.org"]), ("desk.builder", "desktop", [.. names, "example.org"]));
        string[] literals = ["192.168.1.1:443", "10.0.0.5:443", "127.0.0.1:443", "169.254.169.254:80", "100.64.1.1:443", "[::1]:443", "[fd00::5]:443", "[::ffff:10.0.0.1]:443"];
        foreach (var client in new[] { "builder", "desk.builder" })
        {
            foreach (var target in literals) Assert.Equal(403, await rig.StatusAsync($"CONNECT {target} HTTP/1.1\r\n{rig.Auth(client)}\r\n"));
            foreach (var name in names) Assert.Equal(403, await rig.StatusAsync($"CONNECT {name}:443 HTTP/1.1\r\n{rig.Auth(client)}\r\n"));
            Assert.Equal(403, await rig.StatusAsync($"GET http://169.254.169.254/latest/meta-data/ HTTP/1.1\r\n{rig.Auth(client)}\r\n"));
            Assert.Equal(403, await rig.StatusAsync($"GET http://meta.example.org/latest/meta-data/ HTTP/1.1\r\n{rig.Auth(client)}\r\n"));
        }
        Assert.Empty(connector.Seen);
        var log = EgressLog.Read(LogPath).ToList();
        Assert.Equal(2 * (literals.Length + names.Length + 2), log.Count);
        Assert.All(log, e => Assert.Equal("refuse", e.Decision));
        Assert.Contains(log, e => e.Client == "desk.builder" && e.Reason.Contains("metadata", StringComparison.Ordinal));
        Assert.Contains(log, e => e.Client == "builder" && e.Reason.Contains("carrier grade NAT", StringComparison.Ordinal));

        // Under the gate, the host itself: nothing leaves the bot or desktop bridges, desktops reach nothing on the host, bots only the gateway.
        var rules = FirewallRules.Rules(_r.Config);
        Assert.Contains("-A OMABOT-USER -i br-omabot0 -j DROP", rules);
        Assert.Contains("-A OMABOT-USER -i br-omabot1 -j DROP", rules);
        foreach (var range in new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "100.64.0.0/10", "127.0.0.0/8" })
            Assert.Contains($"-A OMABOT-USER -d {range} -i br-omabot2 -j DROP", rules);
        Assert.Equal(1, rules.Count(r => r.Contains("-j ACCEPT", StringComparison.Ordinal) && !r.Contains("ESTABLISHED", StringComparison.Ordinal)));
        Assert.Contains("-A OMABOT-INPUT -i br-omabot1 -j DROP", rules);
    }

    [Fact(DisplayName = "SEC-06: the supervisor starts no bot, gate, desktop or networked check without the firewall rules (fail closed)")]
    public async Task Sec06FailClosed()
    {
        var engine = new FakeContainerEngine();
        var full = string.Join("\n", ["*filter", .. FirewallRules.Rules(_r.Config), "COMMIT"]);
        var firewall = new FixedFirewallReader(string.Join("\n", ["*filter", .. FirewallRules.Rules(_r.Config).Skip(1), "COMMIT"]));
        var sup = new DirectSupervisor(engine, _r.Config.Bots.Network, [_r.Paths.Bots, Path.Combine(_r.AppDir, "checks"), _r.WorkRoot], new NetworkPolicy(_r.Config, firewall), [_r.Paths.Home]);
        var row = _bots.Get("builder");
        var clients = new EgressClients(_r.Paths, _r.Config, Redactor.Shared);
        var bot = ContainerSpec.For(row.Spec, row.Port, "omb_t" + new string('q', 30), _r.Paths, _r.Config, new BotRenderer(_r.Paths, _r.Config), _r.AppDir, false, clients.ProxyUrl("builder"));
        var ex = await Assert.ThrowsAsync<ContainerEngineException>(() => sup.EnsureRunningAsync(bot));
        Assert.Contains("firewall rule", ex.Message);
        await Assert.ThrowsAsync<ContainerEngineException>(() => sup.EnsureServiceAsync(ServiceContainerSpec.Gate(_r.Config, clients)));
        Directory.CreateDirectory(Path.Combine(_r.AppDir, "checks"));
        File.WriteAllText(Path.Combine(_r.AppDir, "checks", "new-todo-lines.sh"), "#!/bin/bash\n");
        var checks = new SupervisorCommandChecks(sup, _r.Config, _r.AppDir, slug => clients.ProxyUrl(EgressClients.Check(slug)));
        var ws = Path.Combine(_r.WorkRoot, "ws");
        Directory.CreateDirectory(ws);
        var check = await checks.RunAsync("builder", "new-todo-lines.sh", ["notes.md"], ws, network: true, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Contains("firewall rule", check.Error);
        Assert.Empty(engine.Created);
        Assert.Empty(engine.RawCreated);

        firewall.Rules = null;
        Assert.Contains("Fail closed", (await Assert.ThrowsAsync<ContainerEngineException>(() => sup.EnsureRunningAsync(bot))).Message);

        firewall.Rules = full;
        await sup.EnsureRunningAsync(bot);
        Assert.Single(engine.Created);
        var withNet = await checks.RunAsync("builder", "new-todo-lines.sh", ["notes.md"], ws, network: true, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Null(withNet.Error);
        var body = Assert.Single(engine.RawCreated).Value;
        Assert.Equal("oma-roster", body["HostConfig"]!["NetworkMode"]!.GetValue<string>());
        Assert.Contains(body["Env"]!.AsArray(), v => v!.GetValue<string>().StartsWith("HTTPS_PROXY=http://check.builder:", StringComparison.Ordinal));

        var cliEnv = new CliEnv { Paths = _r.Paths, AppDir = _r.AppDir, Clock = _r.Clock, Out = new StringWriter(), Err = new StringWriter() };
        Assert.Equal(Exit.Failed, await new CliApp(cliEnv) { FirewallReader = new FixedFirewallReader("*filter\nCOMMIT\n") }.RunAsync(["egress", "check"]));
        Assert.Contains("missing", cliEnv.Err.ToString());
        var okEnv = new CliEnv { Paths = _r.Paths, AppDir = _r.AppDir, Clock = _r.Clock, Out = new StringWriter(), Err = new StringWriter() };
        Assert.Equal(Exit.Ok, await new CliApp(okEnv) { FirewallReader = new FixedFirewallReader(full) }.RunAsync(["egress", "check"]));
    }

    [Fact(DisplayName = "SEC-07: DNS rebinding and redirects to private addresses are refused; the gate connects only to the address it checked")]
    public async Task Sec07()
    {
        await using var web = TestServer.Http("HTTP/1.1 302 Found\r\nLocation: http://169.254.169.254/latest/meta-data/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        var connector = new MapConnector(new() { [("93.184.216.34", 80)] = web.Port, [("93.184.216.34", 443)] = web.Port });
        var flips = 0;
        var rebind = new MapResolver((h, _) => h switch
        {
            "mixed.example.org" => ["93.184.216.34", "10.0.0.5"],
            "flip.example.org" => Interlocked.Increment(ref flips) == 1 ? ["93.184.216.34"] : ["127.0.0.1"],
            "inner.example.org" => ["192.168.1.1"],
            _ => ["93.184.216.34"],
        });
        await using var rig = new EgressRig(LogPath, _r.Clock, rebind, connector, ("builder", "agent", ["mixed.example.org", "flip.example.org", "inner.example.org", "start.example.org"]));

        Assert.Equal(403, await rig.StatusAsync($"CONNECT mixed.example.org:443 HTTP/1.1\r\n{rig.Auth("builder")}\r\n"));
        var (first, c1, _, _) = await rig.SendAsync($"CONNECT flip.example.org:443 HTTP/1.1\r\n{rig.Auth("builder")}\r\n");
        c1.Dispose();
        Assert.Equal(200, first);
        Assert.Equal(403, await rig.StatusAsync($"CONNECT flip.example.org:443 HTTP/1.1\r\n{rig.Auth("builder")}\r\n"));
        Assert.All(connector.Seen, ep => Assert.Equal("93.184.216.34", ep.Address.ToString()));

        var (status, c2, _, _) = await rig.SendAsync($"GET http://start.example.org/go HTTP/1.1\r\n{rig.Auth("builder")}\r\n");
        c2.Dispose();
        Assert.Equal(302, status);
        Assert.Contains("Location: http://169.254.169.254/latest/meta-data/", rig.LastHead);
        Assert.Equal(403, await rig.StatusAsync($"GET http://169.254.169.254/latest/meta-data/ HTTP/1.1\r\n{rig.Auth("builder")}\r\n"));
        Assert.Equal(403, await rig.StatusAsync($"GET http://inner.example.org/ HTTP/1.1\r\n{rig.Auth("builder")}\r\n"));
        var log = EgressLog.Read(LogPath).ToList();
        Assert.Contains(log, e => e.Host == "mixed.example.org" && e.Reason.Contains("refused address", StringComparison.Ordinal));
        Assert.Contains(log, e => e.Host == "flip.example.org" && e.Decision == "refuse" && e.Reason.Contains("loopback", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "SEC-11: Facebook and X are never reachable unless Spencer adds X by hand, and personal Facebook never")]
    public async Task Sec11()
    {
        var team = await Cli("team", "apply", _r.TeamFile(Team.Replace("allow_hosts: [example.org]", "allow_hosts: [example.org, x.com]", StringComparison.Ordinal)));
        Assert.NotEqual(0, team.Code);
        Assert.Contains("blocked host", team.Err);
        var fb = await Cli("team", "apply", _r.TeamFile(Team.Replace("allow_hosts: [example.org]", "allow_hosts: [m.facebook.com]", StringComparison.Ordinal)));
        Assert.Contains("blocked host", fb.Err);

        var connector = new MapConnector([]);
        await using var rig = new EgressRig(LogPath, _r.Clock, new MapResolver((_, _) => ["93.184.216.34"]), connector,
            ("scout", "agent", ["x.com", "api.x.com", "facebook.com", "www.facebook.com", "fb.com", "messenger.com"]));
        foreach (var host in new[] { "x.com", "api.x.com", "facebook.com", "www.facebook.com", "fb.com", "messenger.com", "twitter.com" })
            Assert.Equal(403, await rig.StatusAsync($"CONNECT {host}:443 HTTP/1.1\r\n{rig.Auth("scout")}\r\n"));
        rig.Policy.BlockedHosts = [];
        await using var echo = TestServer.Echo();
        connector = new MapConnector(new() { [("93.184.216.34", 443)] = echo.Port });
        await using var byHand = new EgressRig(LogPath, _r.Clock, new MapResolver((_, _) => ["93.184.216.34"]), connector, ("scout", "agent", ["x.com", "facebook.com"]));
        byHand.Policy.BlockedHosts = [];
        var (status, c, _, _) = await byHand.SendAsync($"CONNECT x.com:443 HTTP/1.1\r\n{byHand.Auth("scout")}\r\n");
        c.Dispose();
        Assert.Equal(200, status);
        Assert.Equal(403, await byHand.StatusAsync($"CONNECT facebook.com:443 HTTP/1.1\r\n{byHand.Auth("scout")}\r\n"));
        Assert.All(EgressPolicy.AlwaysBlocked, h => Assert.True(new EgressPolicy().Blocked("www." + h)));
    }

    [Fact(DisplayName = "Desktop bridge: two fixed tools without a desktop; actions go only to the roster's desktop address on the internal network")]
    public async Task DesktopBridge()
    {
        var desktopUrl = "http://oma-desktop-builder:8000/mcp";
        var rosterCalls = new List<string>();
        var roster = new StubHandler((req, body) =>
        {
            lock (rosterCalls) rosterCalls.Add(req.RequestUri!.AbsolutePath + " " + body);
            if (req.RequestUri!.AbsolutePath == "/v1/turn/claim") return Task.FromResult(StubHandler.Json("""{"token":"tok"}"""));
            return Task.FromResult(StubHandler.Json(new JsonObject { ["ok"] = true, ["text"] = desktopUrl }.ToJsonString()));
        });
        var desktopCalls = new List<string>();
        var desktop = new StubHandler((req, body) =>
        {
            lock (desktopCalls) desktopCalls.Add(req.RequestUri + " " + body);
            var msg = JsonNode.Parse(body)!;
            var id = msg["id"]?.ToJsonString();
            var resp = msg["method"]?.GetValue<string>() switch
            {
                "initialize" => StubHandler.Json("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{\"tools\":{}}}}"),
                "tools/list" => StubHandler.Sse("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\"}",
                    "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"tools\":[{\"name\":\"screenshot\",\"description\":\"Take a screenshot\",\"inputSchema\":{\"type\":\"object\"}}]}}"),
                "tools/call" => StubHandler.Json("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"clicked\"},{\"type\":\"image\",\"data\":\"AAAA\"}]}}"),
                _ => new HttpResponseMessage(HttpStatusCode.Accepted),
            };
            resp.Headers.Add("Mcp-Session-Id", "sess-1");
            return Task.FromResult(resp);
        });
        var bridge = new Omabot.McpShim.DesktopBridge(new ShimClient(new HttpClient(roster) { BaseAddress = new Uri("http://roster") }), new HttpClient(desktop));
        var list = JsonNode.Parse((await bridge.HandleAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}"""))!)!;
        Assert.Equal(new[] { "desktop_list_actions", "desktop_act" }, list["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()));
        Assert.Empty(rosterCalls);
        Assert.Empty(desktopCalls);

        var actions = JsonNode.Parse((await bridge.HandleAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"desktop_list_actions","arguments":{}}}"""))!)!;
        Assert.Contains("screenshot", actions["result"]!["content"]![0]!["text"]!.GetValue<string>());
        var act = JsonNode.Parse((await bridge.HandleAsync("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"desktop_act","arguments":{"action":"click","arguments":{"x":5,"y":6}}}}"""))!)!;
        Assert.Contains("clicked", act["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Contains("image content", act["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.All(desktopCalls, c => Assert.StartsWith(desktopUrl, c));
        Assert.Contains(rosterCalls, c => c.Contains("desktop_endpoint", StringComparison.Ordinal));
        Assert.Contains(desktopCalls, c => c.Contains("\"name\":\"click\"", StringComparison.Ordinal));

        desktopUrl = "http://169.254.169.254/latest/meta-data/";
        var refused = JsonNode.Parse((await bridge.HandleAsync("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"desktop_list_actions","arguments":{}}}"""))!)!;
        Assert.True(refused["result"]!["isError"]!.GetValue<bool>());
        Assert.DoesNotContain(desktopCalls, c => c.Contains("169.254", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "SCN-13: a bot opens an allowed public page on its desktop while Spencer watches on noVNC", Skip = "Needs Docker, the pinned desktop image with cua-driver, the egress gate image and a real public site; the cloud VM has none. The gate, leases, desktop spec and shim bridge are covered by the tests above.")]
    public void Scn13() { }
}
