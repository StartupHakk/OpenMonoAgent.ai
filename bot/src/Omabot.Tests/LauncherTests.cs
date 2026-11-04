// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Acp;
using Omabot.Common;
using Omabot.Containers;
using Omabot.Db;
using Omabot.Supervision;
using Omabot.Tests.Support;

namespace Omabot.Tests;

public sealed class LauncherTests : IDisposable
{
    private readonly TempRoster _r = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    public void Dispose() => _r.Dispose();

    private BotLauncher Launcher(RosterDb db, FakeContainerEngine engine, Func<int, AcpClient> acp, TimeSpan? ready = null)
        => new(db, _r.Config, _r.Paths, _r.Clock, _r.Audit(db), new Notices(db, _r.Clock, Redactor.Shared),
            new DirectSupervisor(engine, _r.Config.Bots.Network), acp, new LaunchOptions(_r.AppDir, ready ?? TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10)));

    [Fact(DisplayName = "Launcher: up creates a safe container, waits for discovery and opens the standing session")]
    public async Task Up()
    {
        var db = await Applied.DefaultPlusTwoAsync(_r);
        await using var srv = await FakeAcpServer.StartAsync();
        var engine = new FakeContainerEngine();
        var l = Launcher(db, engine, _ => new AcpClient(Http, srv.BaseUrl));
        var res = await l.UpAsync("Scout");
        Assert.True(res.Ok, res.Message);
        var bots = new BotStore(db, _r.Clock);
        var row = bots.Get("Scout");
        Assert.Equal(BotStates.Running, row.State);
        Assert.NotNull(bots.GetSession(row.Id));
        var spec = Assert.Single(engine.Created).Value;
        Assert.Empty(spec.SafetyProblems());
        Assert.Equal("oma-bot-scout", spec.Name);
        // Second up reuses the session.
        var session = bots.GetSession(row.Id);
        Assert.True((await l.UpAsync("Scout")).Ok);
        Assert.Equal(session, bots.GetSession(row.Id));
        Assert.Single(srv.Sessions);
    }

    [Fact(DisplayName = "Launcher: no discovery answer fails with backoff, the fifth crash disables with one notice")]
    public async Task FailThenDisable()
    {
        var db = await Applied.DefaultPlusTwoAsync(_r);
        var engine = new FakeContainerEngine();
        var l = Launcher(db, engine, _ => new AcpClient(Http, "http://127.0.0.1:1"), TimeSpan.Zero);
        var r1 = await l.UpAsync("Scout");
        Assert.False(r1.Ok);
        Assert.Contains("Next try in 5 seconds", r1.Message);
        for (var i = 0; i < 3; i++) { _r.Clock.Advance(TimeSpan.FromSeconds(1)); Assert.Equal(BotStates.Failed, (await l.UpAsync("Scout")).State); }
        _r.Clock.Advance(TimeSpan.FromSeconds(1));
        var last = await l.UpAsync("Scout");
        Assert.Equal(BotStates.Disabled, last.State);
        var row = new BotStore(db, _r.Clock).Get("Scout");
        Assert.False(row.Enabled);
        Assert.Equal(1, new Notices(db, _r.Clock, Redactor.Shared).Count());
        Assert.False((await l.UpAsync("Scout")).Ok);
    }

    [Fact(DisplayName = "Health monitor restarts a crashed bot after the backoff delay")]
    public async Task HealthRestart()
    {
        var db = await Applied.DefaultPlusTwoAsync(_r);
        await using var srv = await FakeAcpServer.StartAsync();
        var engine = new FakeContainerEngine();
        AcpClient Acp(int _) => new(Http, srv.BaseUrl);
        var l = Launcher(db, engine, Acp);
        Assert.True((await l.UpAsync("Scout")).Ok);
        var mon = new HealthMonitor(db, _r.Clock, l, Acp);
        Assert.Empty(await mon.TickAsync());
        srv.Ready = false;
        engine.Kill("oma-bot-scout");
        var t1 = await mon.TickAsync();
        Assert.Contains(t1, x => x.Contains("health probe failed", StringComparison.Ordinal));
        Assert.Contains(t1, x => x.Contains("waiting", StringComparison.Ordinal));
        srv.Ready = true;
        _r.Clock.Advance(TimeSpan.FromSeconds(6));
        var t2 = await mon.TickAsync();
        Assert.Contains(t2, x => x.Contains("is running", StringComparison.Ordinal));
        Assert.Equal(BotStates.Running, new BotStore(db, _r.Clock).Get("Scout").State);
    }

    [Fact(DisplayName = "Down stops and removes only the roster container")]
    public async Task Down()
    {
        var db = await Applied.DefaultPlusTwoAsync(_r);
        await using var srv = await FakeAcpServer.StartAsync();
        var engine = new FakeContainerEngine();
        var l = Launcher(db, engine, _ => new AcpClient(Http, srv.BaseUrl));
        await l.UpAsync("Scout");
        var res = await l.DownAsync("Scout");
        Assert.True(res.Ok, res.Message);
        Assert.Equal(BotStates.Stopped, new BotStore(db, _r.Clock).Get("Scout").State);
    }

    [Fact(DisplayName = "Direct supervisor refuses names outside oma-bot-*")]
    public async Task GuardNames()
    {
        var sup = new DirectSupervisor(new FakeContainerEngine(), "n");
        await Assert.ThrowsAnyAsync<Exception>(() => sup.StopAsync("openmono-agent"));
        await Assert.ThrowsAnyAsync<Exception>(() => sup.RemoveAsync("postgres"));
    }

    [Fact(DisplayName = "Per bot tokens: stable, rotated, stored only as hashes in memory and 0600 on disk")]
    public void Tokens()
    {
        var t = new Secrets.BotTokens(_r.Paths, new Redactor());
        var a = t.Ensure("scout");
        Assert.Equal(a, t.Ensure("scout"));
        var b = t.Rotate("scout");
        Assert.NotEqual(a, b);
        var hashes = t.LoadHashes();
        Assert.Equal("scout", hashes[SecureFile.Sha256Hex(b)]);
        Assert.False(hashes.ContainsKey(SecureFile.Sha256Hex(a)));
        var file = _r.Paths.Bot("scout").TokenFile;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }
}
