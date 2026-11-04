// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Microsoft.AspNetCore.Http;
using Omabot.Acp;
using Omabot.Common;
using Omabot.Config;
using Omabot.Containers;
using Omabot.Db;
using Omabot.Doctor;
using Omabot.Gateway;
using Omabot.Routines;
using Omabot.Runtime;
using Omabot.Supervision;
using Omabot.Tests.Support;

namespace Omabot.Tests;

/// <summary>Phase 9: load, soak and health bot cases in simulated time against stub upstreams and fake bots.</summary>
public sealed class OpsTests : IDisposable
{
    private readonly TempRoster _r = new();
    public void Dispose() => _r.Dispose();

    private (PassThroughGateway Gw, StubHandler Up, RosterDb Db, TurnContexts Contexts) Gateway(int slots, Func<HttpRequestMessage, string, Task<HttpResponseMessage>> answer, int bots)
    {
        var c = _r.Config;
        c.Gateway.Slots = null;
        c.Gateway.NodeClasses.Add(new NodeClass { Name = "main-3090", MeasuredSlots = slots });
        c.Gateway.UpstreamNodeClass = "main-3090";
        var up = new StubHandler(answer);
        var db = _r.Db();
        var contexts = new TurnContexts(db, SystemClock.Instance, Priority.Assignment);
        var tokens = Enumerable.Range(0, bots).ToDictionary(i => SecureFile.Sha256Hex($"omb_opsbot{i}_tokentokentoken"), i => $"bot{i}");
        var routes = new Dictionary<string, ModelRoute> { ["flash-next"] = new("flash-next", "Q", "", "", "", ["class=main-3090"]) };
        var gw = new PassThroughGateway(() => tokens, () => routes, _ => "sk-upstreamkey0123456789abcdef", new HttpClient(up), SystemClock.Instance, false, TimeSpan.FromSeconds(2))
        {
            Backends = new BackendRegistry(c, SystemClock.Instance),
            Contexts = contexts,
            Usage = new UsageMeter(db, SystemClock.Instance),
        };
        return (gw, up, db, contexts);
    }

    private static DefaultHttpContext Ctx(int bot)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/v1/chat/completions";
        ctx.Request.Headers.Authorization = $"Bearer omb_opsbot{bot}_tokentokentoken";
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"model":"flash-next","stream":true,"messages":[]}"""));
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    [Fact(DisplayName = "LOAD-02 (simulated upstream) 4 bots at 1 slot: never more than 1 in flight, p95 wait in bounds, no bot over 3x the median wait")]
    public async Task Load02Simulated()
    {
        var (gw, up, db, _) = Gateway(1, async (_, _) => { await Task.Delay(15); return StubHandler.ChatStream(2); }, 4);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(b => Task.Run(async () => { for (var k = 0; k < 8; k++) await gw.HandleAsync(Ctx(b)); })));
        Assert.True(up.Peak <= 1, $"server saw {up.Peak} in flight");
        var waits = db.Query("SELECT bot, AVG(queue_ms) FROM usage GROUP BY bot;", r => r.GetDouble(1)).OrderBy(x => x).ToList();
        Assert.Equal(4, waits.Count);
        var median = (waits[1] + waits[2]) / 2;
        Assert.True(waits.Max() <= Math.Max(3 * median, 60), $"waits {string.Join(", ", waits)}");
        var all = db.Query("SELECT queue_ms FROM usage ORDER BY queue_ms;", r => r.GetInt64(0));
        Assert.True(all[(int)(all.Count * 0.95) - 1] < 20_000);
        Assert.Equal(32, all.Count);
    }

    [Fact(DisplayName = "LOAD-03 (simulated upstream) 8 bots with routines at 2 slots: interactive goes first, routines are held back, never more than 2 in flight")]
    public async Task Load03Simulated()
    {
        var (gw, up, db, contexts) = Gateway(2, async (_, _) => { await Task.Delay(15); return StubHandler.ChatStream(2); }, 8);
        Priority[] mix = [Priority.Interactive, Priority.Interactive, Priority.Assignment, Priority.Assignment, Priority.Assignment, Priority.Routine, Priority.Routine, Priority.Routine];
        for (var b = 0; b < 8; b++) contexts.Set($"bot{b}", mix[b], mix[b] == Priority.Routine ? "r" + b : null, TimeSpan.FromMinutes(10));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(b => Task.Run(async () => { for (var k = 0; k < 6; k++) await gw.HandleAsync(Ctx(b)); })));
        Assert.True(up.Peak <= 2, $"server saw {up.Peak} in flight");
        var byPriority = db.Query("SELECT priority, AVG(queue_ms) FROM usage GROUP BY priority;", r => (P: r.GetString(0), Wait: r.GetDouble(1))).ToDictionary(x => x.P, x => x.Wait);
        Assert.True(byPriority["interactive"] <= byPriority["routine"], string.Join(", ", byPriority.Select(kv => $"{kv.Key} {kv.Value:0}")));
        var interactive = db.Query("SELECT queue_ms FROM usage WHERE priority='interactive' ORDER BY queue_ms;", r => r.GetInt64(0));
        Assert.True(interactive[(int)(interactive.Count * 0.95) - 1] < 15_000);
        Assert.Equal(48, Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM usage;")));
    }

    [Fact(DisplayName = "SOAK-03 (simulated) inference restart in a busy hour: clean errors, the gateway recovers, a routine records error once")]
    public async Task Soak03Simulated()
    {
        var down = false;
        var (gw, _, _, _) = Gateway(2, (_, _) => down ? throw new HttpRequestException("connection refused") : Task.FromResult(StubHandler.ChatStream(2)), 2);
        var before = Ctx(0);
        await gw.HandleAsync(before);
        Assert.Equal(200, before.Response.StatusCode);
        down = true;
        var failed = new List<DefaultHttpContext>();
        for (var i = 0; i < 6; i++)
        {
            var ctx = Ctx(i % 2);
            await gw.HandleAsync(ctx);
            failed.Add(ctx);
            await Task.Delay(400);
        }
        Assert.All(failed, f => Assert.Equal(503, f.Response.StatusCode));
        failed[0].Response.Body.Position = 0;
        Assert.Contains("inference_unreachable", new StreamReader(failed[0].Response.Body).ReadToEnd());
        Assert.True(gw.InferenceUnreachable);
        down = false;
        var after = Ctx(1);
        await gw.HandleAsync(after);
        Assert.Equal(200, after.Response.StatusCode);
        Assert.False(gw.InferenceUnreachable);

        var db = _r.Db();
        var env = new Cli.CliEnv { Paths = _r.Paths, AppDir = _r.AppDir, Clock = _r.Clock, Out = new StringWriter(), Err = new StringWriter() };
        await new Cli.CliApp(env).RunAsync(["team", "apply", _r.TeamFile("version: 1\nmodels:\n  flash-next:\n    upstream_model: Q\nbots:\n  - name: Scout\n    personality: You scout.\n    model: flash-next\n    tools: { allow: [FileRead] }\n    routines:\n      - { name: digest, interval_minutes: 30, instructions: Write the digest. }\n")]);
        var runner = new FakeRoutineRunner { Behavior = _ => Task.FromResult(new RoutineRunResult(RoutineStore.Status.Error, "inference unreachable", "")) };
        var notices = new Notices(db, _r.Clock, Redactor.Shared);
        var scheduler = new RoutineScheduler(db, _r.Config, _r.Paths, _r.Clock, _r.Audit(db), notices, runner,
            new CheckEnvironment { Paths = _r.Paths, Clock = _r.Clock, Db = db, AppDir = _r.AppDir, WorkspaceRoots = _r.Config.WorkspaceRoots }, () => 2);
        scheduler.Recover();
        for (var i = 0; i < 3; i++)
        {
            _r.Clock.Advance(TimeSpan.FromMinutes(31));
            await scheduler.TickAsync(CancellationToken.None);
            await scheduler.WhenIdleAsync();
        }
        var runs = db.Query("SELECT status FROM routine_runs WHERE routine='digest' ORDER BY id;", r => r.GetString(0));
        Assert.True(runs.Count >= 2);
        Assert.All(runs, st => Assert.Equal(RoutineStore.Status.Error, st));
        Assert.Equal(runs.Count, runner.Calls.Count);
        Assert.Equal(runs.Count, notices.Count("routine_run:"));
    }

    private (RoutineScheduler S, FakeRoutineRunner Runner, Func<List<CheckResult>> Set) HealthScheduler(RosterDb db, List<CheckResult> results)
    {
        var runner = new FakeRoutineRunner();
        var checks = new CheckEnvironment
        {
            Paths = _r.Paths, Clock = _r.Clock, Db = db, AppDir = _r.AppDir, WorkspaceRoots = _r.Config.WorkspaceRoots,
            Doctor = _ => Task.FromResult<IReadOnlyList<CheckResult>>(results.ToList()),
        };
        return (new RoutineScheduler(db, _r.Config, _r.Paths, _r.Clock, _r.Audit(db), new Notices(db, _r.Clock, Redactor.Shared), runner, checks, () => 2), runner, () => results);
    }

    [Fact(DisplayName = "HEALTH-03 and HEALTH-04: a healthy run makes no model call; a relay, container or disk problem is reported once, on change")]
    public async Task Health0304()
    {
        var env = new Cli.CliEnv { Paths = _r.Paths, AppDir = _r.AppDir, Clock = _r.Clock, Out = new StringWriter(), Err = new StringWriter() };
        Assert.Equal(0, await new Cli.CliApp(env).RunAsync(["init"]));
        var db = _r.Db();
        Assert.Equal(0, await new Cli.CliApp(env).RunAsync(["team", "apply", Path.Combine(_r.Paths.Home, "team", "team.yaml")]));
        var results = new List<CheckResult> { new("inference", CheckStatus.Ok, "reachable"), new("containers", CheckStatus.Ok, "all up"), new("disk", CheckStatus.Ok, "40% used") };
        var (s, runner, _) = HealthScheduler(db, results);
        s.Recover();
        async Task Tick() { _r.Clock.Advance(TimeSpan.FromMinutes(6)); await s.TickAsync(CancellationToken.None); await s.WhenIdleAsync(); }
        for (var i = 0; i < 4; i++) await Tick();
        Assert.Empty(runner.Calls);
        Assert.All(db.Query("SELECT status FROM routine_runs WHERE routine='health-watch';", r => r.GetString(0)), st => Assert.Equal(RoutineStore.Status.Quiet, st));
        Assert.Equal(0, Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM usage;")));

        results[0] = new CheckResult("inference", CheckStatus.Fail, "inference unreachable through the relay");
        results[1] = new CheckResult("containers", CheckStatus.Fail, "oma-bot-scout is down");
        results[2] = new CheckResult("disk", CheckStatus.Fail, "disk 96% full");
        await Tick();
        var report = Assert.Single(runner.Calls);
        Assert.Equal("health", report.Bot.Slug);
        Assert.Contains("inference unreachable", report.CheckOutput);
        Assert.Contains("oma-bot-scout is down", report.CheckOutput);
        Assert.Contains("disk 96% full", report.CheckOutput);
        await Tick();
        Assert.Single(runner.Calls);
        results[0] = new CheckResult("inference", CheckStatus.Ok, "reachable");
        results[1] = new CheckResult("containers", CheckStatus.Ok, "all up");
        results[2] = new CheckResult("disk", CheckStatus.Ok, "40% used");
        await Tick();
        Assert.True(runner.Calls.Count <= 2);
    }

    [Fact(DisplayName = "SOAK-02 (simulated) 72 hours with a container kill every 2 hours: each bot back within a minute, no accepted work lost")]
    public async Task Soak02Simulated()
    {
        await using var x = await Roster.StartAsync(_r);
        var engine = new FakeContainerEngine();
        var launcher = new BotLauncher(x.Db, _r.Config, _r.Paths, _r.Clock, _r.Audit(x.Db), x.Notices, new DirectSupervisor(engine, _r.Config.Bots.Network),
            port => new AcpClient(new HttpClient(), x.Servers[x.Bots.All().Single(b => b.Port == port).Slug].BaseUrl), new LaunchOptions(_r.AppDir, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10)));
        x.EnsureRunning = async (name, ct) => { var r = await launcher.UpAsync(name, ct); return r.Ok ? null : r.Message; };
        foreach (var b in x.Bots.All()) Assert.True((await launcher.UpAsync(b.Name)).Ok);
        var monitor = new HealthMonitor(x.Db, _r.Clock, launcher, port => new AcpClient(new HttpClient(), x.Servers[x.Bots.All().Single(b => b.Port == port).Slug].BaseUrl));
        var random = new Random(7);
        var slugs = x.Bots.All().Select(b => b.Slug).ToList();
        var accepted = new List<string>();
        for (var hour = 0; hour < 72; hour += 2)
        {
            _r.Clock.Advance(TimeSpan.FromHours(2));
            var victim = slugs[random.Next(slugs.Count)];
            engine.Kill("oma-bot-" + victim);
            x.Servers[victim].Ready = false;
            await monitor.TickAsync();
            Assert.Equal(BotStates.Failed, x.Bots.Get(victim).State);
            var e = x.Runtime.Enqueue(new EnqueueRequest(victim, "work", $"Check in after kill {hour}", Rework: true));
            Assert.True(e.Ok, e.Message);
            accepted.Add(e.Id!);
            await x.Runtime.TickAsync(CancellationToken.None);
            await x.Runtime.WhenIdleAsync();
            Assert.Equal(AssignmentStatus.Queued, x.Runtime.Store.Get(e.Id!)!.Status);
            _r.Clock.Advance(TimeSpan.FromSeconds(6));
            x.Servers[victim].Ready = true;
            await monitor.TickAsync();
            Assert.Equal(BotStates.Running, x.Bots.Get(victim).State);
            await x.DrainAsync();
        }
        Assert.All(accepted, id => Assert.Equal(AssignmentStatus.Completed, x.Runtime.Store.Get(id)!.Status));
        Assert.All(x.Bots.All(), b => Assert.True(b.Enabled));
        x.AssertInvariants();
    }

    [Fact(DisplayName = "LOAD-05: agent box resources at 8 bots and 2 desktops stay under 80 percent of the host", Skip = "Needs a Docker host with the agent image and the desktop image; measure with oma-bot status and docker stats on the roster host.")]
    public void Load05() { }

    [Fact(DisplayName = "SCN-15: FRPC stopped on the inference box: every bot sees inference unreachable within a minute and work resumes with the tunnel", Skip = "Needs the FRPS and FRPC relay and the inference box. The gateway half is covered by SOAK-03 (simulated) and the health report by HEALTH-03.")]
    public void Scn15() { }

    [Fact(DisplayName = "INT-14: the health bot reports relay down, container down, disk low and queue age with zero model calls when healthy (live)", Skip = "Needs Docker, the agent image and the relay; the routine logic is covered by HEALTH-03 and HEALTH-04 above.")]
    public void Int14() { }

    [Fact(DisplayName = "SOAK-02 and SOAK-03 live: 72 hours with real container kills and a real inference server restart", Skip = "Needs a Docker host running the roster for 72 hours and the inference server through the relay.")]
    public void SoakLive() { }
}
