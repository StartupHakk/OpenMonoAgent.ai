// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Omabot.Cli;
using Omabot.Common;
using Omabot.Config;
using Omabot.Containers;
using Omabot.Db;
using Omabot.Desktops;
using Omabot.Egress;
using Omabot.Gateway;
using Omabot.Render;
using Omabot.Supervision;
using Omabot.Team;
using Omabot.Tests.Support;

namespace Omabot.Tests;

/// <summary>Phase 7: the egress gate, firewall rules, roster networks, desktops, and the gateway's search and scrape.</summary>
public sealed class EgressTests : IAsyncLifetime
{
    public const string Team = """
        version: 1
        models:
          flash-next:
            upstream_model: Qwen3.8-Flash-Next
        bots:
          - name: Builder
            personality: You are Builder.
            model: flash-next
            desktop: own
            web: { search: true, scrape: true, allow_hosts: [docs.example.org, "*.nuget.org"] }
            tools: { allow: [FileRead, WebSearch, WebFetch] }
          - name: Scout
            personality: You are Scout.
            model: flash-next
            desktop: shared
            web: { allow_hosts: [example.org] }
            tools: { allow: [FileRead] }
          - name: Editor
            personality: You are Editor.
            model: flash-next
            desktop: shared
            tools: { allow: [FileRead] }
          - name: Helper
            personality: You are Helper.
            model: flash-next
            desktop: pool
            tools: { allow: [FileRead] }
          - name: Writer
            personality: You are Writer.
            model: flash-next
            desktop: pool
            tools: { allow: [FileRead] }
          - name: Reader
            personality: You are Reader.
            model: flash-next
            desktop: pool
            tools: { allow: [FileRead] }
          - name: Health
            role: health
            personality: You check health.
            model: flash-next
            memory: { enabled: false }
            tools: { allow: [mcp__roster__list_bots] }
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

    // ---------------------------------------------------------------- address policy and the gate

    [Fact(DisplayName = "UNIT-EG-01: every range in plan 7.7 is refused, in IPv4, IPv6 and IPv4 inside IPv6 forms")]
    public void UnitEg01()
    {
        string[] refused =
        [
            "127.0.0.1", "127.255.255.254", "::1", "10.0.0.1", "10.255.0.9", "172.16.0.1", "172.31.255.255", "192.168.1.5", "fc00::1", "fd12:3456::1",
            "169.254.169.254", "169.254.0.1", "fe80::1", "100.64.0.1", "100.127.255.254", "0.0.0.0", "0.1.2.3", "224.0.0.1", "239.255.255.250", "255.255.255.255",
            "::ffff:10.0.0.1", "::ffff:127.0.0.1", "::ffff:169.254.169.254", "64:ff9b::a00:1", "2002:a00:1::1", "::a00:1", "ff02::1", "::",
        ];
        foreach (var ip in refused) Assert.NotNull(AddressPolicy.Refusal(IPAddress.Parse(ip)));
        Assert.Contains("metadata", AddressPolicy.Refusal(IPAddress.Parse("169.254.169.254")));
        Assert.Contains("carrier grade NAT", AddressPolicy.Refusal(IPAddress.Parse("100.64.0.1")));
        foreach (var ip in new[] { "93.184.216.34", "1.1.1.1", "8.8.8.8", "2606:4700:4700::1111", "172.32.0.1", "100.128.0.1", "192.169.0.1" })
            Assert.Null(AddressPolicy.Refusal(IPAddress.Parse(ip)));
    }

    [Fact(DisplayName = "UNIT-EG-02: a public address on the allowlist is allowed, by CONNECT and by plain http, to exactly the checked address")]
    public async Task UnitEg02()
    {
        await using var echo = TestServer.Echo();
        await using var web = TestServer.Http("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nConnection: close\r\n\r\nhello");
        var connector = new MapConnector(new() { [("93.184.216.34", 443)] = echo.Port, [("93.184.216.34", 80)] = web.Port });
        await using var rig = new EgressRig(LogPath, _r.Clock, new MapResolver((_, _) => ["93.184.216.34"]), connector, ("scout", "agent", ["example.org", "*.example.net"]));

        var (status, client, stream, _) = await rig.SendAsync("CONNECT example.org:443 HTTP/1.1\r\nHost: example.org:443\r\n" + rig.Auth("scout") + "\r\n");
        using (client)
        {
            Assert.Equal(200, status);
            await stream.WriteAsync("ping"u8.ToArray());
            var buf = new byte[4];
            await stream.ReadExactlyAsync(buf);
            Assert.Equal("ping", Encoding.ASCII.GetString(buf));
        }
        var (s2, c2, _, rest) = await rig.SendAsync("GET http://api.example.net/hello?x=1 HTTP/1.1\r\nHost: api.example.net\r\n" + rig.Auth("scout") + "Accept: */*\r\n\r\n");
        using (c2)
        {
            Assert.Equal(200, s2);
            Assert.StartsWith("hello", rest);
        }
        var sent = Assert.Single(web.Received);
        Assert.StartsWith("GET /hello?x=1 HTTP/1.1\r\nHost: api.example.net\r\n", sent);
        Assert.DoesNotContain("Proxy-Authorization", sent);
        Assert.Contains("Connection: close", sent);
        Assert.All(connector.Seen, ep => Assert.Equal("93.184.216.34", ep.Address.ToString()));
        var log = EgressLog.Read(LogPath).ToList();
        Assert.Equal(2, log.Count(e => e.Decision == "allow" && e.Address == "93.184.216.34"));
        Assert.DoesNotContain(log, e => e.Reason.Contains(rig.Tokens["scout"], StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Tokens["scout"], File.ReadAllText(LogPath));
    }

    [Fact(DisplayName = "UNIT-EG-03: a name not on the allowlist is refused before any lookup or connection, and logged")]
    public async Task UnitEg03()
    {
        var connector = new MapConnector([]);
        var resolver = new MapResolver((_, _) => ["93.184.216.34"]);
        await using var rig = new EgressRig(LogPath, _r.Clock, resolver, connector, ("scout", "agent", ["example.org"]));
        Assert.Equal(403, await rig.StatusAsync("CONNECT evil.example.com:443 HTTP/1.1\r\n" + rig.Auth("scout") + "\r\n"));
        Assert.Equal(403, await rig.StatusAsync("CONNECT sub.example.org:443 HTTP/1.1\r\n" + rig.Auth("scout") + "\r\n"));
        Assert.Equal(0, resolver.Calls);
        Assert.Empty(connector.Seen);
        Assert.All(EgressLog.Read(LogPath), e => { Assert.Equal("refuse", e.Decision); Assert.Contains("allowlist", e.Reason); });
    }

    [Fact(DisplayName = "Gate: credentials, ports, request shape and connection limits are enforced")]
    public async Task GateRules()
    {
        await using var echo = TestServer.Echo();
        var connector = new MapConnector(new() { [("93.184.216.34", 443)] = echo.Port });
        await using var rig = new EgressRig(LogPath, _r.Clock, new MapResolver((_, _) => ["93.184.216.34"]), connector, ("scout", "agent", ["example.org"]), ("check.scout", "check", ["example.org"]));
        Assert.Equal(407, await rig.StatusAsync("CONNECT example.org:443 HTTP/1.1\r\n\r\n"));
        var wrong = "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("scout:" + rig.Tokens["check.scout"])) + "\r\n";
        Assert.Equal(407, await rig.StatusAsync("CONNECT example.org:443 HTTP/1.1\r\n" + wrong + "\r\n"));
        Assert.Equal(403, await rig.StatusAsync("CONNECT example.org:22 HTTP/1.1\r\n" + rig.Auth("scout") + "\r\n"));
        Assert.Equal(400, await rig.StatusAsync("GET https://example.org/ HTTP/1.1\r\n" + rig.Auth("scout") + "\r\n"));
        Assert.Equal(400, await rig.StatusAsync("GET /relative HTTP/1.1\r\n" + rig.Auth("scout") + "\r\n"));
        Assert.Equal(400, await rig.StatusAsync("HELLO\r\n\r\n"));
        Assert.Equal(400, await rig.StatusAsync("GET http://user:pw@example.org/ HTTP/1.1\r\n" + rig.Auth("scout") + "\r\n"));
        Assert.Equal(400, await rig.StatusAsync("CONNECT example.org:443 HTTP/1.1\r\nX-Big: " + new string('a', EgressProxy.MaxHead) + "\r\n\r\n"));
        Assert.Contains(EgressLog.Read(LogPath), e => e.Reason == "port 22 is not allowed");

        await using var one = new EgressRig(LogPath, _r.Clock, new MapResolver((_, _) => ["93.184.216.34"]), connector, 1, ("scout", "agent", ["example.org"]));
        var (s1, c1, _, _) = await one.SendAsync("CONNECT example.org:443 HTTP/1.1\r\n" + one.Auth("scout") + "\r\n");
        using (c1)
        {
            Assert.Equal(200, s1);
            Assert.Equal(429, await one.StatusAsync("CONNECT example.org:443 HTTP/1.1\r\n" + one.Auth("scout") + "\r\n"));
        }
    }

    [Fact(DisplayName = "Egress policy: tokens stay 0600 and out of the policy, the health bot gets no client, blocked hosts leave allowlists")]
    public void PolicyWriter()
    {
        var clients = new EgressClients(_r.Paths, _r.Config, Redactor.Shared);
        var desktops = new DesktopManager(_db, _r.Config, _r.Paths, _r.Clock, _r.Audit(_db));
        desktops.Sync(_bots.All());
        var builder = _bots.Get("builder");
        builder.Spec.AllowHosts.Add("x.com");
        var policy = clients.Build([.. _bots.All().Select(b => b.Slug == "builder" ? builder : b)], desktops.All().Select(d => new DesktopClient(d.Name, desktops.AllowFrom(d))));
        Assert.DoesNotContain(policy.Clients, c => c.Name.Contains("health", StringComparison.Ordinal));
        Assert.Contains(policy.Clients, c => c.Name == "builder" && c.Kind == "agent");
        Assert.Contains(policy.Clients, c => c.Name == "check.builder" && c.Kind == "check");
        Assert.Contains(policy.Clients, c => c.Name == "desk.builder" && c.Allow.Contains("docs.example.org"));
        Assert.Contains(policy.Clients, c => c.Name == "desk.shared" && c.Allow.Count == 0);
        Assert.DoesNotContain("x.com", policy.Clients.Single(c => c.Name == "builder").Allow);
        Assert.True(clients.Write(policy));
        Assert.False(clients.Write(policy));
        var json = File.ReadAllText(clients.PolicyPath);
        foreach (var token in Directory.GetFiles(clients.Dir).Select(f => File.ReadAllText(f).Trim()))
            Assert.DoesNotContain(token, json);
        Assert.All(Directory.GetFiles(clients.Dir), f => Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(f)));
        Assert.StartsWith("http://builder:", clients.ProxyUrl("builder"));
        Assert.EndsWith("@egress:3128", clients.ProxyUrl("builder"));
    }

    [Fact(DisplayName = "Refusal alarm: 20 refusals in 10 minutes notify Spencer once per client")]
    public void RefusalAlarm()
    {
        var notices = new Notices(_db, _r.Clock, Redactor.Shared);
        var monitor = new EgressMonitor(LogPath, _r.Config, _r.Clock, notices, _r.Audit(_db));
        var log = new EgressLog(LogPath, _r.Clock);
        for (var i = 0; i < 19; i++) log.Write("scout", "agent", "10.0.0.5", 443, false, "IP addresses are never on an allowlist");
        Assert.Empty(monitor.Tick());
        log.Write("scout", "agent", "10.0.0.5", 443, false, "IP addresses are never on an allowlist");
        Assert.Single(monitor.Tick());
        for (var i = 0; i < 25; i++) log.Write("scout", "agent", "10.0.0.5", 443, false, "again");
        monitor.Tick();
        Assert.Equal(1, notices.Count("egress_refusals:scout"));
        _r.Clock.Advance(TimeSpan.FromMinutes(11));
        for (var i = 0; i < 10; i++) log.Write("editor", "agent", "a.test", 443, false, "x");
        _r.Clock.Advance(TimeSpan.FromMinutes(11));
        for (var i = 0; i < 10; i++) log.Write("editor", "agent", "a.test", 443, false, "x");
        monitor.Tick();
        Assert.Equal(0, notices.Count("egress_refusals:editor"));
    }

    // ---------------------------------------------------------------- firewall and networks

    private static string Dump(RosterConfig c, int skip = -1)
    {
        var rules = FirewallRules.Rules(c).Where((_, i) => i != skip);
        return "*filter\n:INPUT ACCEPT [0:0]\n:DOCKER-USER - [0:0]\n:OMABOT-USER - [0:0]\n:OMABOT-INPUT - [0:0]\n-A INPUT -i lo -j ACCEPT\n"
               + string.Join("\n", rules.Select(r => r.Replace(" ", "  ", StringComparison.Ordinal))) + "\n-A DOCKER-USER -j RETURN\nCOMMIT\n";
    }

    [Fact(DisplayName = "Firewall rules: rendered in iptables-save order, verified line for line, and the agent-host rule only when asked")]
    public void FirewallRulesVerify()
    {
        var c = _r.Config;
        Assert.Empty(FirewallRules.Missing(c, Dump(c)));
        var missing = FirewallRules.Missing(c, Dump(c, skip: 2));
        Assert.Equal("-A OMABOT-USER -i br-omabot0 -j DROP", Assert.Single(missing));
        Assert.Contains("-A OMABOT-USER -d 169.254.0.0/16 -i br-omabot2 -j DROP", FirewallRules.Rules(c));
        Assert.Contains("-A OMABOT-INPUT -d 172.30.240.1/32 -i br-omabot0 -p tcp -m tcp --dport 7478 -j ACCEPT", FirewallRules.Rules(c));
        Assert.Contains("-A OMABOT-INPUT -i br-omabot1 -j DROP", FirewallRules.Rules(c));
        Assert.DoesNotContain(FirewallRules.Rules(c), r => r.Contains("7476", StringComparison.Ordinal));
        c.HostExec.OpenFirewall = true;
        Assert.Contains(FirewallRules.Rules(c), r => r.Contains("--dport 7476", StringComparison.Ordinal));
        Assert.NotEmpty(FirewallRules.Missing(c, Dump(new RosterConfig { WorkspaceRoots = c.WorkspaceRoots })));
        var script = FirewallRules.Script(c);
        Assert.Contains("iptables -I DOCKER-USER 1 -j OMABOT-USER", script);
        Assert.Contains("iptables -A OMABOT-INPUT -i br-omabot2 -j DROP", script);
    }

    [Fact(DisplayName = "Roster networks: fixed private subnets and br- bridges, no overlap, and the gateway on the bots network gateway")]
    public void NetworkConfig()
    {
        var ok = new RosterConfig { WorkspaceRoots = ["/srv/work"] };
        ok.Validate();
        Assert.Equal("172.30.240.1", Networks.Bots(ok).Gateway);
        Assert.True(Networks.Bots(ok).Internal);
        Assert.True(Networks.Desktops(ok).Internal);
        Assert.False(Networks.Egress(ok).Internal);
        RosterConfig Bad(Action<RosterConfig> change)
        {
            var c = new RosterConfig { WorkspaceRoots = ["/srv/work"] };
            change(c);
            return c;
        }
        Assert.Throws<InvalidDataException>(Bad(c => c.Desktops.Subnet = "172.30.240.0/24").Validate);
        Assert.Throws<InvalidDataException>(Bad(c => c.Bots.Bridge = "omabot0").Validate);
        Assert.Throws<InvalidDataException>(Bad(c => c.Bots.Bridge = "br-waytoolongname").Validate);
        Assert.Throws<InvalidDataException>(Bad(c => c.Gateway.BindAddress = "172.17.0.1").Validate);
        Assert.Throws<InvalidDataException>(Bad(c => { c.Bots.Subnet = "8.8.8.0/24"; c.Gateway.BindAddress = "8.8.8.1"; }).Validate);
        Assert.Throws<InvalidDataException>(Bad(c => c.Egress.Subnet = "172.30.242.1/24").Validate);
    }

    private (DirectSupervisor Sup, FakeContainerEngine Engine, FixedFirewallReader Firewall) Supervised(bool rules = true)
    {
        var engine = new FakeContainerEngine();
        var firewall = new FixedFirewallReader(rules ? Dump(_r.Config) : null, "cannot read");
        var sup = new DirectSupervisor(engine, _r.Config.Bots.Network, [_r.Paths.Bots], new NetworkPolicy(_r.Config, firewall), [_r.Paths.Home]);
        return (sup, engine, firewall);
    }

    private ContainerSpec BotSpecFor(string slug)
    {
        var row = _bots.Get(slug);
        return ContainerSpec.For(row.Spec, row.Port, "omb_t" + new string('q', 30), _r.Paths, _r.Config, new BotRenderer(_r.Paths, _r.Config), _r.AppDir, false,
            new EgressClients(_r.Paths, _r.Config, Redactor.Shared).ProxyUrl(slug));
    }

    [Fact(DisplayName = "Supervisor: creates the three networks, joins a desktop bot to the desktop network, and refuses a wrong network")]
    public async Task SupervisorNetworks()
    {
        var (sup, engine, _) = Supervised();
        var builder = BotSpecFor("builder");
        await sup.EnsureRunningAsync(builder);
        Assert.Equal(3, engine.Networks.Count);
        Assert.True(engine.Networks["oma-roster"].Internal);
        Assert.Equal("br-omabot1", engine.Networks["oma-desk"].Bridge);
        Assert.False(engine.Networks["oma-egress"].Internal);
        Assert.Contains(engine.Connected, x => x.Network == "oma-desk");
        Assert.Equal("172.30.240.1", builder.Env["NO_PROXY"]);
        Assert.StartsWith("http://builder:", builder.Env["HTTPS_PROXY"]);
        Assert.DoesNotContain(builder.ToDockerRunArgs(), a => a.Contains(builder.Env["HTTPS_PROXY"], StringComparison.Ordinal));

        await Assert.ThrowsAsync<ContainerEngineException>(() => sup.EnsureRunningAsync(BotSpecFor("scout") with { Network = "bridge" }));
        await Assert.ThrowsAsync<ContainerEngineException>(() => sup.EnsureRunningAsync(BotSpecFor("scout") with { ExtraNetworks = ["oma-egress"] }));

        var (sup2, engine2, _) = Supervised();
        engine2.Networks["oma-roster"] = new NetworkInfo("oma-roster", false, "172.30.240.0/24", "br-omabot0");
        var ex = await Assert.ThrowsAsync<ContainerEngineException>(() => sup2.EnsureRunningAsync(BotSpecFor("scout")));
        Assert.Contains("must be internal", ex.Message);
    }

    [Fact(DisplayName = "Service specs: the gate and desktops pass the checks; ports, mounts, networks, users and credentials are refused when wrong")]
    public void ServiceSpecs()
    {
        var clients = new EgressClients(_r.Paths, _r.Config, Redactor.Shared);
        var gate = ServiceContainerSpec.Gate(_r.Config, clients);
        string[] roots = [_r.Paths.Home];
        Assert.Empty(gate.SafetyProblems(_r.Config, roots));
        Assert.Equal("oma-egress", gate.ToEngineJson()["HostConfig"]!["NetworkMode"]!.GetValue<string>());
        Assert.True(gate.ToEngineJson()["HostConfig"]!["ReadonlyRootfs"]!.GetValue<bool>());
        Assert.NotEmpty((gate with { Ports = [new PortMap(3128, 3128)] }).SafetyProblems(_r.Config, roots));
        Assert.NotEmpty((gate with { Networks = [new NetworkAttach("oma-roster", [])] }).SafetyProblems(_r.Config, roots));

        var desktops = new DesktopManager(_db, _r.Config, _r.Paths, _r.Clock, _r.Audit(_db));
        desktops.Sync(_bots.All());
        var service = new DesktopService(desktops, NullSupervisorForTests.Instance, clients, new DesktopSecrets(_r.Paths, Redactor.Shared), _r.Config, _r.Paths, _r.Audit(_db));
        var d = desktops.Get("builder")!;
        var spec = service.SpecFor(d);
        Assert.Empty(spec.SafetyProblems(_r.Config, roots));
        Assert.Equal("127.0.0.1", spec.ToEngineJson()["HostConfig"]!["PortBindings"]!["6901/tcp"]![0]!["HostIp"]!.GetValue<string>());
        Assert.Equal(Path.Combine(_r.Paths.Bot("builder").Root, "desktop"), spec.Mounts[0].Source);
        Assert.NotEmpty((spec with { Mounts = [.. spec.Mounts, new Mount("/var/run/docker.sock", "/var/run/docker.sock", false)] }).SafetyProblems(_r.Config, roots));
        Assert.NotEmpty((spec with { Mounts = [new Mount("/etc", "/host-etc", true)] }).SafetyProblems(_r.Config, roots));
        Assert.NotEmpty((spec with { Networks = [new NetworkAttach("oma-roster", [])] }).SafetyProblems(_r.Config, roots));
        Assert.NotEmpty((spec with { Ports = [new PortMap(80, 6901)] }).SafetyProblems(_r.Config, roots));
        Assert.NotEmpty((spec with { User = "0:0" }).SafetyProblems(_r.Config, roots));
        Assert.NotEmpty((spec with { Env = new Dictionary<string, string>(spec.Env) { ["OPENMONO_API_KEY"] = "x" } }).SafetyProblems(_r.Config, roots));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(new DesktopSecrets(_r.Paths, Redactor.Shared).FileFor("builder")));
        var pw = new DesktopSecrets(_r.Paths, Redactor.Shared).Ensure("builder");
        Assert.DoesNotContain(pw, Redactor.Shared.Redact("pw " + pw));
    }

    private sealed class NullSupervisorForTests : ISupervisorClient
    {
        public static readonly NullSupervisorForTests Instance = new();
        public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<string> EnsureRunningAsync(ContainerSpec spec, CancellationToken ct = default) => Task.FromResult("x");
        public Task StopAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ContainerInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ContainerInfo>>([]);
        public Task<string?> ImageIdAsync(string image, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<Routines.CommandCheckResult> RunCheckAsync(CheckContainerSpec spec, CancellationToken ct = default) => throw new InvalidOperationException();
        public Task EnsureNetworksAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> EnsureServiceAsync(ServiceContainerSpec spec, CancellationToken ct = default) => Task.FromResult("svc");
        public Task StopServiceAsync(string name, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ---------------------------------------------------------------- desktops

    [Fact(DisplayName = "Desktops: own, shared one at a time and a pool, with leases that end with their work or after their time, and idle stop")]
    public async Task DesktopLeases()
    {
        var desktops = new DesktopManager(_db, _r.Config, _r.Paths, _r.Clock, _r.Audit(_db));
        Assert.Equal(4, desktops.Sync(_bots.All()).Count);
        Assert.Equal(new[] { "builder", "pool-1", "pool-2", "shared" }, desktops.All().Select(d => d.Name));
        Assert.All(desktops.All(), d => Assert.InRange(d.Port, 7600, 7607));

        Assert.Equal("builder", desktops.Acquire(_bots.Get("builder"), "a1").Desktop!.Name);
        Assert.Equal("shared", desktops.Acquire(_bots.Get("scout"), "a2").Desktop!.Name);
        var busy = desktops.Acquire(_bots.Get("editor"), "a3");
        Assert.Null(busy.Desktop);
        Assert.Contains("in use by scout", busy.Problem);
        Assert.Equal("shared", desktops.Acquire(_bots.Get("scout"), "a2").Desktop!.Name);
        Assert.Equal("pool-1", desktops.Acquire(_bots.Get("helper"), "a4").Desktop!.Name);
        Assert.Equal("pool-2", desktops.Acquire(_bots.Get("writer"), "a5").Desktop!.Name);
        Assert.Contains("pool desktop is in use", desktops.Acquire(_bots.Get("reader"), "a6").Problem);
        Assert.Contains("desktop: off", desktops.Acquire(_bots.Get("health"), null).Problem);
        Assert.Equal("scout", desktops.AllowFrom(desktops.Get("shared")!));

        Assert.Single(desktops.ExpireLeases(id => id != "a2"));
        Assert.Equal("shared", desktops.Acquire(_bots.Get("editor"), "a3").Desktop!.Name);
        _r.Clock.Advance(TimeSpan.FromMinutes(_r.Config.Desktops.LeaseMinutes + 1));
        Assert.Equal(4, desktops.ExpireLeases(_ => true).Count);
        Assert.Empty(desktops.ActiveLeases());

        var (sup, engine, _) = Supervised();
        var clients = new EgressClients(_r.Paths, _r.Config, Redactor.Shared);
        var service = new DesktopService(desktops, sup, clients, new DesktopSecrets(_r.Paths, Redactor.Shared), _r.Config, _r.Paths, _r.Audit(_db));
        var (url, problem) = await service.EndpointAsync(_bots.Get("builder"), "a7", CancellationToken.None);
        Assert.Null(problem);
        Assert.Equal("http://oma-desktop-builder:8000/mcp", url);
        Assert.Contains("oma-desktop-builder", engine.RawCreated.Keys);
        Assert.Equal("running", desktops.Get("builder")!.State);
        Assert.True(Directory.Exists(Path.Combine(_r.Paths.Bot("builder").Root, "desktop")));
        _r.Clock.Advance(TimeSpan.FromMinutes(_r.Config.Desktops.IdleMinutes + _r.Config.Desktops.LeaseMinutes + 1));
        var lines = await service.TickAsync(_ => false, CancellationToken.None);
        Assert.Contains(lines, l => l.Contains("stopped after", StringComparison.Ordinal));
        Assert.Equal("stopped", desktops.Get("builder")!.State);
        Assert.Contains(engine.Calls, c => c == "stop oma-desktop-builder");
        Assert.True(Directory.Exists(Path.Combine(_r.Paths.Bot("builder").Root, "desktop")));
    }

    [Fact(DisplayName = "Desktops: a team asking for more than 8 is refused, and the health bot never gets one")]
    public async Task DesktopCap()
    {
        var many = string.Concat(Enumerable.Range(1, 9).Select(i => $"\n  - name: D{i}\n    personality: You are D{i}.\n    model: flash-next\n    desktop: own\n    tools: {{ allow: [FileRead] }}"));
        var big = await Cli("team", "apply", _r.TeamFile("version: 1\nmodels:\n  flash-next:\n    upstream_model: Qwen3.8-Flash-Next\nbots:" + many));
        Assert.NotEqual(0, big.Code);
        Assert.Contains("the cap is 8", big.Err);
        var health = await Cli("team", "apply", _r.TeamFile(Team.Replace("    memory: { enabled: false }", "    memory: { enabled: false }\n    desktop: own", StringComparison.Ordinal)));
        Assert.Contains("has no desktop", health.Err);
    }

    [Fact(DisplayName = "Rendering: a desktop bot gets the desktop server through the shim, and the hook allows desktop tools only for it")]
    public async Task DesktopRendering()
    {
        File.WriteAllText(Path.Combine(_r.AppDir, "hooks", "pre-tool.sh"), File.ReadAllText(Path.Combine(Applied.RepoRoot(), "bot", "hooks", "pre-tool.sh")));
        var renderer = new BotRenderer(_r.Paths, _r.Config);
        var settings = JsonNode.Parse(renderer.RenderSettings(_bots.Get("builder").Spec, new RenderContext("http://172.30.240.1:7478/v1", true, null, [])))!;
        Assert.Equal("desktop", settings["mcp_servers"]!["desktop"]!["env"]!["ROSTER_MCP_MODE"]!.GetValue<string>());
        Assert.Null(JsonNode.Parse(renderer.RenderSettings(_bots.Get("health").Spec, new RenderContext("http://172.30.240.1:7478/v1", true, null, [])))!["mcp_servers"]!["desktop"]);
        Assert.Contains("mcp__desktop__*", BotRenderer.AllowedTools(_bots.Get("builder").Spec));
        Assert.DoesNotContain("mcp__desktop__*", BotRenderer.AllowedTools(_bots.Get("health").Spec));

        var list = Path.Combine(_r.Root, "allowed.txt");
        async Task<int> Hook(string tool, string allowed)
        {
            File.WriteAllText(list, allowed);
            var psi = new ProcessStartInfo("bash") { RedirectStandardError = true };
            psi.ArgumentList.Add(Path.Combine(Applied.RepoRoot(), "bot", "hooks", "pre-tool.sh"));
            psi.ArgumentList.Add(tool);
            psi.Environment["ROSTER_ALLOWED_TOOLS"] = list;
            psi.Environment["ROSTER_TURN_GATE"] = Path.Combine(_r.Root, "no-gate");
            psi.Environment["ROSTER_FLAGS_DIR"] = Path.Combine(_r.Root, "flags");
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            return p.ExitCode;
        }
        Assert.Equal(0, await Hook("mcp__desktop__desktop_act", "FileRead\nmcp__desktop__*\n"));
        Assert.Equal(2, await Hook("mcp__other__desktop_act", "FileRead\nmcp__desktop__*\n"));
        Assert.Equal(2, await Hook("mcp__desktop__desktop_act", "FileRead\n"));
        Assert.Equal(2, await Hook("Bash", "*\n"));
        Assert.Equal(2, await Hook("mcp__desktop__x", "mcp__desktop\n"));
    }

    // ---------------------------------------------------------------- gateway search and scrape

    private (PassThroughGateway Gw, StubHandler Up, string Token) WebGateway()
    {
        var up = new StubHandler((req, _) => Task.FromResult(StubHandler.Json(req.RequestUri!.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
            ? """{"results":[{"title":"Docs","url":"https://docs.example.org/"}]}"""
            : """{"status":200,"content":"page text"}""")));
        var token = "omb_" + new string('w', 32);
        var routes = new Dictionary<string, ModelRoute>(StringComparer.OrdinalIgnoreCase) { ["flash-next"] = new("flash-next", "Qwen3.8-Flash-Next", "", "", "", ["class=main-3090"]) };
        var gw = new PassThroughGateway(() => new Dictionary<string, string> { [SecureFile.Sha256Hex(token)] = "builder", [SecureFile.Sha256Hex(token + "s")] = "scout" },
            () => routes, _ => "sk-web0123456789abcdefghij", new HttpClient(up), _r.Clock, false, TimeSpan.FromSeconds(30))
        {
            Web = new GatewayWeb("https://relay.example.test/searxng", "https://relay.example.test/scrapling", "secrets/inference.key", slug => _bots.Find(slug)?.Spec, () => ["x.com"]),
        };
        return (gw, up, token);
    }

    private static async Task<(int Status, string Body)> Call(PassThroughGateway gw, string token, string method, string path, string? body = null, string query = "")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        ctx.Request.QueryString = new QueryString(query);
        ctx.Request.Headers.Authorization = "Bearer " + token;
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? ""));
        ctx.Response.Body = new MemoryStream();
        await gw.HandleAsync(ctx);
        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync());
    }

    [Fact(DisplayName = "Gateway web: services follow the team file, search goes to SearXNG, scrape only for allowlisted hosts and never an IP")]
    public async Task GatewayWebServices()
    {
        var (gw, up, token) = WebGateway();
        var services = await Call(gw, token, "GET", "/v1/services");
        Assert.Equal("""{"search":true,"scrape":true}""", services.Body);
        Assert.Equal("""{"search":false,"scrape":false}""", (await Call(gw, token + "s", "GET", "/v1/services")).Body);

        var search = await Call(gw, token, "GET", "/v1/search", query: "?q=dotnet%20sdk&format=json&engines=all");
        Assert.Equal(200, search.Status);
        Assert.Contains("docs.example.org", search.Body);
        var sent = up.Seen.Last();
        // StubHandler records Uri.ToString(), which shows the escaped query unescaped.
        Assert.Equal("https://relay.example.test/searxng/search?q=dotnet sdk&format=json", sent.Url);
        Assert.Equal("Bearer sk-web0123456789abcdefghij", sent.Auth);
        Assert.Equal(403, (await Call(gw, token + "s", "GET", "/v1/search", query: "?q=x")).Status);
        Assert.Equal(400, (await Call(gw, token, "GET", "/v1/search", query: "?q=")).Status);

        var page = await Call(gw, token, "POST", "/v1/scrape", """{"url":"https://api.nuget.org/v3/index.json","max_length":999999999,"format":"exe","render":true}""");
        Assert.Equal(200, page.Status);
        var forwarded = JsonNode.Parse(up.Seen.Last().Body)!;
        Assert.Equal(200000, forwarded["max_length"]!.GetValue<int>());
        Assert.Equal("markdown", forwarded["format"]!.GetValue<string>());
        foreach (var bad in new[] { "https://evil.example.com/", "http://10.0.0.5/", "http://[::1]/", "https://x.com/home", "https://www.facebook.com/", "file:///etc/passwd", "https://u:p@docs.example.org/" })
        {
            var r = await Call(gw, token, "POST", "/v1/scrape", new JsonObject { ["url"] = bad }.ToJsonString());
            Assert.True(r.Status is 400 or 403, bad);
        }
        Assert.Equal(2, up.Seen.Count);
    }
}
