// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Containers;
using Omabot.Db;
using Omabot.Render;
using Omabot.Supervision;
using Omabot.Tests.Support;

namespace Omabot.Tests;

public sealed class ContainerSpecTests : IDisposable
{
    private readonly TempRoster _r = new();
    public void Dispose() => _r.Dispose();

    private async Task<(ContainerSpec A, ContainerSpec B)> TwoSpecsAsync()
    {
        var db = await Applied.DefaultPlusTwoAsync(_r);
        var bots = new BotStore(db, _r.Clock);
        var renderer = new BotRenderer(_r.Paths, _r.Config);
        ContainerSpec Make(string n)
        {
            var row = bots.Get(n);
            return ContainerSpec.For(row.Spec, row.Port, "tok_" + new string('x', 30), _r.Paths, _r.Config, renderer, _r.AppDir, false);
        }
        return (Make("Scout"), Make("Builder"));
    }

    [Fact(DisplayName = "UNIT-SUP-01 rendered specs: distinct mounts, host-bridge shadowed, logs on tmpfs, no docker.sock, ports on 127.0.0.1")]
    public async Task Sup01()
    {
        var (a, b) = await TwoSpecsAsync();
        foreach (var s in new[] { a, b })
        {
            Assert.Empty(s.SafetyProblems());
            Assert.Contains(s.Mounts, m => m.Source == "/dev/null" && m.Target == "/home/agent/.openmono/host-bridge.json" && m.ReadOnly);
            Assert.Contains("/home/agent/.openmono/logs", s.Tmpfs);
            Assert.Contains("/home/agent/.openmono/host-bridge/logs", s.Tmpfs);
            Assert.DoesNotContain(s.Mounts, m => m.Source.Contains("docker.sock", StringComparison.Ordinal));
            Assert.Equal("127.0.0.1", s.HostIp);
            Assert.Equal(["ALL"], s.CapDrop);
            Assert.False(s.User.StartsWith("0:", StringComparison.Ordinal));
        }
        string Src(ContainerSpec s, string target) => s.Mounts.Single(m => m.Target == target).Source;
        Assert.NotEqual(Src(a, ContainerSpec.DataDir), Src(b, ContainerSpec.DataDir));
        Assert.NotEqual(Src(a, ContainerSpec.Workspace), Src(b, ContainerSpec.Workspace));
        Assert.NotEqual(a.HostPort, b.HostPort);
        Assert.NotEqual(a.Name, b.Name);
    }

    [Fact(DisplayName = "SEC-10 a spec with docker.sock is refused at every layer")]
    public async Task Sec10()
    {
        var (a, _) = await TwoSpecsAsync();
        var bad = a with { Mounts = [.. a.Mounts, new Mount("/var/run/docker.sock", "/var/run/docker.sock", false)] };
        Assert.Contains(bad.SafetyProblems(), p => p.Contains("docker.sock", StringComparison.Ordinal));
        var engine = new FakeContainerEngine();
        var sup = new DirectSupervisor(engine, "oma-bot-net");
        await Assert.ThrowsAnyAsync<Exception>(() => sup.EnsureRunningAsync(bad));
        Assert.Empty(engine.Created);
        var docker = new DockerEngineClient(Path.Combine(_r.Root, "nope.sock"));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => docker.CreateAsync(bad));
        Assert.Contains("docker.sock", ex.Message);
    }

    [Theory(DisplayName = "SEC-10 other unsafe specs are refused")]
    [InlineData("hostip")]
    [InlineData("root")]
    [InlineData("caps")]
    [InlineData("shadow")]
    [InlineData("tmpfs")]
    [InlineData("runfolder")]
    public async Task Sec10Variants(string kind)
    {
        var (a, _) = await TwoSpecsAsync();
        var bad = kind switch
        {
            "hostip" => a with { HostIp = "0.0.0.0" },
            "root" => a with { User = "0:0" },
            "caps" => a with { CapDrop = [] },
            "shadow" => a with { Mounts = [.. a.Mounts.Where(m => m.Source != "/dev/null")] },
            "tmpfs" => a with { Tmpfs = ["/tmp"] },
            _ => a with { Mounts = [.. a.Mounts, new Mount(_r.Paths.Run + "/supervisor.sock", "/x", true)] },
        };
        Assert.NotEmpty(bad.SafetyProblems());
    }

    [Fact(DisplayName = "SEC-08 bots get distinct data dirs (live cross-read test needs Docker)")]
    public async Task Sec08Spec()
    {
        var (a, b) = await TwoSpecsAsync();
        Assert.DoesNotContain(a.Mounts, m => b.Mounts.Any(n => n.Source == m.Source && m.Source != "/dev/null" && !m.ReadOnly));
    }

    [Fact(DisplayName = "SEC-08 live: bot A cannot read bot B data dir", Skip = "Needs Docker and the openmono-agent image; Docker is not available on the build box.")]
    public void Sec08Live() { }

    [Fact(DisplayName = "SEC-09 host-bridge.json is shadowed by /dev/null in the spec")]
    public async Task Sec09Spec()
    {
        var (a, _) = await TwoSpecsAsync();
        Assert.Contains(a.Mounts, m => m.Target.EndsWith("host-bridge.json", StringComparison.Ordinal) && m.Source == "/dev/null");
        Assert.Contains(a.ToDockerRunArgs(), x => x.Contains("/dev/null:/home/agent/.openmono/host-bridge.json:ro", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "SEC-09 live: host-bridge.json reads empty inside a bot", Skip = "Needs Docker and the openmono-agent image; Docker is not available on the build box.")]
    public void Sec09Live() { }

    [Fact(DisplayName = "SEC-01 ACP ports bind 127.0.0.1 in spec and engine JSON")]
    public async Task Sec01Spec()
    {
        var (a, _) = await TwoSpecsAsync();
        var json = a.ToEngineJson().ToJsonString();
        Assert.Contains("\"HostIp\":\"127.0.0.1\"", json);
        Assert.DoesNotContain("0.0.0.0", json);
    }

    [Fact(DisplayName = "SEC-01 live: ACP and gateway ports unreachable from the LAN", Skip = "Needs Docker, a running roster and a second LAN host.")]
    public void Sec01Live() { }

    [Fact(DisplayName = "Docker run args redact the bot token")]
    public async Task RunArgsRedact()
    {
        var (a, _) = await TwoSpecsAsync();
        var args = string.Join(" ", a.ToDockerRunArgs(redactEnv: true));
        Assert.DoesNotContain("tok_xxxx", args);
    }

    [Fact(DisplayName = "INT-08 rendered spec matches OMA compose shadowing rules (read only)")]
    public async Task Int08()
    {
        var compose = File.ReadAllText(Path.Combine(Applied.RepoRoot(), "docker", "docker-compose.yml"));
        var agent = compose[compose.IndexOf("\n  agent:", StringComparison.Ordinal)..];
        var (a, _) = await TwoSpecsAsync();
        Assert.Contains("/dev/null:/home/agent/.openmono/host-bridge.json", agent);
        foreach (var shadow in ContainerSpec.ShadowedPaths)
            Assert.Contains("/dev/null:" + shadow, agent);
        foreach (var line in agent.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("target: /home/agent/.openmono", StringComparison.Ordinal))
                Assert.Contains(t["target: ".Length..], a.Tmpfs);
        }
        Assert.Contains("/home/agent/.openmono", agent);
        Assert.Contains(a.Mounts, m => m.Target == "/home/agent/.openmono");
        Assert.Contains(a.Mounts, m => m.Target == "/workspace");
        // Our example compose mirrors the same rules.
        var ours = File.ReadAllText(Path.Combine(Applied.RepoRoot(), "bot", "docker", "docker-compose.yml"));
        Assert.Contains("/dev/null:/home/agent/.openmono/host-bridge.json:ro", ours);
        foreach (var t in ContainerSpec.TmpfsPaths) Assert.Contains(t, ours);
        Assert.DoesNotContain("docker.sock:", ours);
        Assert.Contains("127.0.0.1:", ours);
    }

    [Fact(DisplayName = "Spec label changes when the bot spec changes")]
    public async Task SpecLabel()
    {
        var (a, b) = await TwoSpecsAsync();
        Assert.NotEqual(a.Labels["oma-bot.spec"], b.Labels["oma-bot.spec"]);
        Assert.Equal("1", a.Labels["oma-bot.roster"]);
    }

    [Fact(DisplayName = "Memory limits parse")]
    public void Memory()
    {
        Assert.Equal(2L * 1024 * 1024 * 1024, ContainerSpec.ParseMemory("2g"));
        Assert.Equal(512L * 1024 * 1024, ContainerSpec.ParseMemory("512m"));
    }
}

public sealed class BackoffTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "UNIT-SUP-02 crash backoff is 5 s, 30 s, 2 min, 10 min, then disabled")]
    public void Sup02()
    {
        var fails = new List<DateTimeOffset>();
        var expected = new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10) };
        foreach (var e in expected)
        {
            fails.Add(Now.AddSeconds(-fails.Count));
            var d = Backoff.Next(fails, Now);
            Assert.False(d.Disable);
            Assert.Equal(e, d.Delay);
        }
        fails.Add(Now);
        Assert.True(Backoff.Next(fails, Now).Disable);
    }

    [Fact(DisplayName = "UNIT-SUP-02 failures older than 30 minutes do not count")]
    public void Sup02Window()
    {
        var old = Enumerable.Range(0, 6).Select(i => Now.AddMinutes(-31 - i)).ToList();
        var d = Backoff.Next(old, Now);
        Assert.False(d.Disable);
        Assert.Equal(0, d.FailuresInWindow);
    }

    [Fact(DisplayName = "Bot state moves follow the state chart")]
    public void States()
    {
        Assert.True(BotStates.CanMove(BotStates.Defined, BotStates.Starting));
        Assert.True(BotStates.CanMove(BotStates.Starting, BotStates.Running));
        Assert.True(BotStates.CanMove(BotStates.Running, BotStates.Failed));
        Assert.False(BotStates.CanMove(BotStates.Disabled, BotStates.Running));
    }
}
