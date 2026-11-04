// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Omabot.Approvals;
using Omabot.Cli;
using Omabot.Common;
using Omabot.Config;
using Omabot.Db;
using Omabot.Fleet;
using Omabot.Gateway;
using Omabot.Secrets;
using Omabot.Tests.Support;

namespace Omabot.SecurityTests;

/// <summary>A runner the tests script: each step calls the given function.</summary>
public sealed class ScriptedRunner(Func<StepContext, Task<StepOutcome>> run) : IStepRunner
{
    public Task<StepOutcome> RunAsync(StepContext step, CancellationToken ct) => run(step);
}

/// <summary>A fleet controller on loopback ports with mutual TLS, and real node agents enrolled against it.</summary>
public sealed class FleetRig : IAsyncDisposable
{
    public TempRoster R { get; } = new();
    public ManualClock Clock { get; } = new(DateTimeOffset.UtcNow);
    public RosterDb Db { get; private set; } = null!;
    public AuditLog Audit { get; private set; } = null!;
    public Notices Notices { get; private set; } = null!;
    public FleetController Fleet { get; private set; } = null!;
    public int Base { get; private set; }
    public string CaFingerprint => FleetCa.Fingerprint(Fleet.Ca.Ca);
    private WebApplication _app = null!;

    private static int FreeRun(int count)
    {
        var random = new Random();
        for (var tries = 0; tries < 50; tries++)
        {
            var start = random.Next(30000, 60000);
            var ok = true;
            for (var p = start; p < start + count && ok; p++)
            {
                try
                {
                    using var l = new TcpListener(IPAddress.Loopback, p);
                    l.Start();
                    l.Stop();
                }
                catch (SocketException) { ok = false; }
            }
            if (ok) return start;
        }
        throw new InvalidOperationException("no free port run");
    }

    public static async Task<FleetRig> StartAsync(int maxNodes = 4)
    {
        var f = new FleetRig();
        f.R.Config.Placement.Mode = "fleet";
        f.R.Config.Fleet.EnabledBy = "Spencer";
        f.R.Config.Fleet.MaxNodes = maxNodes;
        f.Base = FreeRun(maxNodes + 1);
        f.R.Config.Fleet.ControllerPort = f.Base;
        f.R.Config.Save(f.R.Paths);
        f.Db = f.R.Db();
        f.Audit = new AuditLog(f.Db, f.Clock, Redactor.Shared);
        f.Notices = new Notices(f.Db, f.Clock, Redactor.Shared);
        var ca = FleetCa.LoadOrCreate(f.R.Paths, f.Clock);
        f.Fleet = new FleetController(f.Db, f.R.Config, f.R.Paths, f.Clock, f.Audit, f.Notices, ca);
        f._app = FleetServer.Build(f.Fleet, ca.ServerCertificate(f.R.Paths, f.R.Config.Fleet.ControllerName), IPAddress.Loopback, f.Base, maxNodes);
        await f._app.StartAsync();
        return f;
    }

    /// <summary>Dials loopback on the port in the address while TLS checks the controller's name, as the relay would.</summary>
    public static HttpMessageHandler Loopback(HttpMessageHandler handler)
    {
        ((SocketsHttpHandler)handler).ConnectCallback = async (ctx, ct) =>
        {
            var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await s.ConnectAsync(IPAddress.Loopback, ctx.DnsEndPoint.Port, ct);
            return new NetworkStream(s, ownsSocket: true);
        };
        return handler;
    }

    public Uri EnrollUrl => new($"https://{R.Config.Fleet.ControllerName}:{Base}/fleet/v1/enroll");
    public Uri NodeUrl(int slot) => new($"https://{R.Config.Fleet.ControllerName}:{Base + slot}");
    public string Home(string node) => Path.Combine(R.Root, "node-" + node);

    public async Task<NodeIdentity> EnrollAsync(string node, IReadOnlyDictionary<string, string>? labels = null, bool neverTouch = false, bool approve = true)
    {
        var code = Fleet.Invite(node, labels ?? new Dictionary<string, string>(), neverTouch, "person:spencer");
        var id = await NodeAgent.EnrollAsync(EnrollUrl, CaFingerprint, node, code, Home(node), Loopback);
        if (approve) Fleet.Approve(node, FleetCa.Fingerprint(id.Certificate), "person:spencer");
        return id;
    }

    public NodeAgent Agent(NodeIdentity id, IStepRunner runner, int? slot = null) => new(id, NodeUrl(slot ?? id.Slot), runner, Home(id.Name), Loopback, Clock);

    public HttpClient Raw(X509Certificate2? cert, int port)
        => new(Loopback(NodeAgent.Handler(cert, (leaf, _) => NodeAgent.ChainsTo(leaf, Fleet.Ca.Ca)))) { BaseAddress = new Uri($"https://{R.Config.Fleet.ControllerName}:{port}") };

    public FleetJob ReadOnly(string key, JsonObject? extra = null)
    {
        var spec = new JsonObject { ["idempotency_key"] = key, ["kind"] = "readonly_query", ["inputs"] = new JsonObject { ["prompt"] = "How full is the disk?" } };
        foreach (var (k, v) in extra ?? []) spec[k] = v?.DeepClone();
        return Fleet.AddJob(spec, "person:spencer");
    }

    /// <summary>A job that needs a person: approved here the way Spencer would, on the card bound to its hash.</summary>
    public FleetJob Approved(JsonObject spec)
    {
        var job = Fleet.AddJob(spec, "person:spencer");
        var store = new ApprovalStore(Db, Clock, Redactor.Shared);
        var card = store.Get(job.ApprovalId!)!;
        Assert.True(store.Decide(card.Id, Approver.Person("spencer"), true, null, card.ActionHash, new ApproverPolicy(Db, Clock, R.Config)).Ok);
        Fleet.Tick();
        return Fleet.Job(job.Id)!;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        R.Dispose();
    }
}

/// <summary>Phase 12: optional fleet mode behind its guards (FLEET-01 to FLEET-08).</summary>
public sealed class Phase12FleetTests
{
    private static readonly Dictionary<string, string> Scrape = new() { ["role"] = "scrape" };

    [Fact(DisplayName = "Fleet guard: off by default; it starts only when a person turns it on by name, and its commands are the CLI's alone")]
    public async Task Guard()
    {
        using var r = new TempRoster();
        var db = r.Db();
        var fleet = new FleetController(db, r.Config, r.Paths, r.Clock, r.Audit(db), new Notices(db, r.Clock, Redactor.Shared), FleetCa.LoadOrCreate(r.Paths, r.Clock));
        Assert.NotNull(fleet.GuardProblem());
        Assert.Throws<FleetException>(() => fleet.Invite("echo11", new Dictionary<string, string>(), false, "person:spencer"));
        Assert.Throws<FleetException>(() => fleet.AddJob(new JsonObject { ["idempotency_key"] = "k", ["kind"] = "readonly_query" }, "person:spencer"));
        Assert.False(fleet.Enroll("fnc_x", "echo11", "csr").Ok);
        var bad = r.Config.Clone();
        bad.Placement.Mode = "fleet";
        Assert.Throws<InvalidDataException>(bad.Validate);

        CliEnv Env() => new() { Paths = r.Paths, AppDir = r.AppDir, Clock = r.Clock, Out = new StringWriter(), Err = new StringWriter() };
        var status = Env();
        Assert.Equal(Exit.Failed, await new CliApp(status).RunAsync(["fleet", "status"]));
        Assert.Contains("Fleet mode is off", status.Err.ToString());
        Assert.Equal(Exit.Invalid, await new CliApp(Env()).RunAsync(["fleet", "enable", "--by", "Spencer"]));
        Assert.Equal("one_host", RosterConfig.Load(r.Paths).Placement.Mode);
        Assert.Equal(0, await new CliApp(Env()).RunAsync(["fleet", "enable", "--by", "Spencer", "--confirm"]));
        Assert.Equal("Spencer", RosterConfig.Load(r.Paths).Fleet.EnabledBy);
        var invite = Env();
        Assert.Equal(0, await new CliApp(invite).RunAsync(["fleet", "invite", "echo11", "--labels", "role=scrape"]));
        Assert.Contains("fnc_", invite.Out.ToString());
        var yaml = Path.Combine(r.Root, "job.yaml");
        File.WriteAllText(yaml, "idempotency_key: shs/deploy/1\nkind: deploy\nsteps:\n  - id: send\n    outward: true\n");
        Assert.Equal(0, await new CliApp(Env()).RunAsync(["fleet", "job", "add", yaml]));
        var job = new FleetController(r.Db(), RosterConfig.Load(r.Paths), r.Paths, r.Clock, r.Audit(r.Db()), new Notices(r.Db(), r.Clock, Redactor.Shared), FleetCa.LoadOrCreate(r.Paths, r.Clock)).Jobs().Single();
        Assert.True(job.Spec["steps"]![0]!["outward"]!.GetValue<bool>());
        Assert.Equal("waiting_approval", job.State);
        File.WriteAllText(yaml, "idempotency_key: shs/deploy/2\nkind: deploy\nsteps:\n  - id: send\n    outward: maybe\n");
        Assert.Equal(Exit.Failed, await new CliApp(Env()).RunAsync(["fleet", "job", "add", yaml]));
        Assert.Equal(0, await new CliApp(Env()).RunAsync(["fleet", "disable"]));
        Assert.Equal(Exit.Failed, await new CliApp(Env()).RunAsync(["fleet", "jobs"]));
    }

    [Fact(DisplayName = "FLEET-01 enrollment with an expired, reused or wrong-node code is refused, and a node works only after a person approves its key")]
    public async Task Fleet01()
    {
        await using var f = await FleetRig.StartAsync();
        var code = f.Fleet.Invite("echo11", Scrape, false, "person:spencer");
        await Assert.ThrowsAsync<FleetException>(() => NodeAgent.EnrollAsync(f.EnrollUrl, f.CaFingerprint, "echo12", code, f.Home("x1"), FleetRig.Loopback));
        var id = await NodeAgent.EnrollAsync(f.EnrollUrl, f.CaFingerprint, "echo11", code, f.Home("echo11"), FleetRig.Loopback);
        Assert.True(SecureFile.IsPrivate(Path.Combine(f.Home("echo11"), "node.pfx")));
        await Assert.ThrowsAsync<FleetException>(() => NodeAgent.EnrollAsync(f.EnrollUrl, f.CaFingerprint, "echo11", code, f.Home("x2"), FleetRig.Loopback));
        var pinned = f.Fleet.Invite("echo14", Scrape, false, "person:spencer");
        await Assert.ThrowsAsync<FleetException>(() => NodeAgent.EnrollAsync(f.EnrollUrl, new string('0', 64), "echo14", pinned, f.Home("x4"), FleetRig.Loopback));
        // The wrong pin stopped before the code was sent, so the code still works with the right pin.
        await NodeAgent.EnrollAsync(f.EnrollUrl, f.CaFingerprint, "echo14", pinned, f.Home("echo14"), FleetRig.Loopback);
        var late = f.Fleet.Invite("echo13", Scrape, false, "person:spencer");
        f.Clock.Advance(TimeSpan.FromMinutes(16));
        await Assert.ThrowsAsync<FleetException>(() => NodeAgent.EnrollAsync(f.EnrollUrl, f.CaFingerprint, "echo13", late, f.Home("x3"), FleetRig.Loopback));
        Assert.Equal(3, f.Db.Query("SELECT detail FROM audit WHERE action='fleet.enroll_refused';", q => q.GetString(0)).Count);

        Assert.Equal("pending", f.Fleet.Node("echo11")!.State);
        var job = f.ReadOnly("check/disk");
        var ran = 0;
        var agent = f.Agent(id, new ScriptedRunner(_ => { ran++; return Task.FromResult(new StepOutcome(true, Text: "ok")); }));
        Assert.Null(await agent.RunOnceAsync());
        Assert.Equal("queued", f.Fleet.Job(job.Id)!.State);
        Assert.Throws<FleetException>(() => f.Fleet.Approve("echo11", new string('a', 64), "person:spencer"));
        f.Fleet.Approve("echo11", FleetCa.Fingerprint(id.Certificate), "person:spencer");
        Assert.Equal(job.Id, await agent.RunOnceAsync());
        Assert.Equal(1, ran);
        Assert.Equal("done", f.Fleet.Job(job.Id)!.State);
    }

    [Fact(DisplayName = "FLEET-02 no certificate, a certificate from another CA, the wrong relay port, an expired or revoked certificate: all refused")]
    public async Task Fleet02()
    {
        await using var f = await FleetRig.StartAsync();
        var a = await f.EnrollAsync("echo11", Scrape);
        var b = await f.EnrollAsync("echo12", Scrape);
        async Task<HttpStatusCode> Lease(X509Certificate2? cert, int port)
        {
            using var http = f.Raw(cert, port);
            return (await http.PostAsJsonAsync("/fleet/v1/lease", new { })).StatusCode;
        }
        Assert.Equal(HttpStatusCode.NoContent, await Lease(a.Certificate, f.Base + a.Slot));
        Assert.Equal(HttpStatusCode.Forbidden, await Lease(null, f.Base + a.Slot));
        Assert.Equal(HttpStatusCode.Forbidden, await Lease(a.Certificate, f.Base + b.Slot));
        Assert.Equal(HttpStatusCode.Forbidden, await Lease(a.Certificate, f.Base));

        using var other = new TempRoster();
        var rogue = FleetCa.LoadOrCreate(other.Paths, f.Clock);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = rogue.SignNode("echo11", new CertificateRequest("CN=echo11", key, HashAlgorithmName.SHA256).CreateSigningRequestPem()).CopyWithPrivateKey(key);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => Lease(X509CertificateLoader.LoadPkcs12(forged.Export(X509ContentType.Pfx), null), f.Base + a.Slot));

        f.Clock.Advance(TimeSpan.FromDays(31));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => Lease(a.Certificate, f.Base + a.Slot));
        f.Clock.Advance(TimeSpan.FromDays(-31));

        Assert.True(File.Exists(f.R.Paths.Bot(FleetController.NodeTokenPrefix + "echo12").TokenFile));
        f.Fleet.Revoke("echo12", "person:spencer");
        Assert.Equal(HttpStatusCode.Forbidden, await Lease(b.Certificate, f.Base + b.Slot));
        Assert.False(File.Exists(f.R.Paths.Bot(FleetController.NodeTokenPrefix + "echo12").TokenFile));
    }

    [Fact(DisplayName = "FLEET-03 lease expiry, a stale fencing token and two nodes claiming one job: only one valid writer")]
    public async Task Fleet03()
    {
        await using var f = await FleetRig.StartAsync();
        await f.EnrollAsync("echo11", Scrape);
        await f.EnrollAsync("echo12", Scrape);
        var a = f.Fleet.Node("echo11")!;
        var b = f.Fleet.Node("echo12")!;
        var job = f.ReadOnly("check/one");
        var first = f.Fleet.Lease(a)!;
        Assert.Equal(1, first.Fencing);
        Assert.Null(f.Fleet.Lease(b));
        Assert.True(f.Fleet.Step(a, job.Id, "run", "intent", 1, null).Ok);

        f.Clock.Advance(TimeSpan.FromSeconds(61));
        f.Fleet.Tick();
        Assert.Equal("queued", f.Fleet.Job(job.Id)!.State);
        var second = f.Fleet.Lease(b)!;
        Assert.Equal(2, second.Fencing);
        Assert.Equal(409, f.Fleet.Step(a, job.Id, "run", "done", 1, null).Status);
        Assert.Equal(409, f.Fleet.Renew(a, job.Id, 1).Status);
        Assert.Equal(409, f.Fleet.Complete(a, job.Id, 1, []).Status);
        Assert.Equal(409, f.Fleet.Step(b, job.Id, "run", "done", 1, null).Status);
        Assert.True(f.Fleet.Step(b, job.Id, "run", "intent", 2, null).Ok);
        Assert.True(f.Fleet.Step(b, job.Id, "run", "done", 2, null).Ok);
        Assert.True(f.Fleet.Complete(b, job.Id, 2, new JsonObject { ["ok"] = true }).Ok);
        Assert.Equal("done", f.Fleet.Job(job.Id)!.State);
        Assert.NotEmpty(f.Db.Query("SELECT id FROM audit WHERE action='fleet.stale_write';", q => q.GetInt64(0)));

        var race = f.ReadOnly("check/race");
        var claims = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() => f.Fleet.Lease(i % 2 == 0 ? a : b))));
        Assert.Single(claims, c => c is not null && c.JobId == race.Id);
    }

    private static JsonObject Outward(string key, params string[] steps) => new()
    {
        ["idempotency_key"] = key, ["kind"] = "deploy",
        ["steps"] = new JsonArray([.. steps.Select(s => (JsonNode)new JsonObject { ["id"] = s.TrimEnd('!'), ["outward"] = s.EndsWith('!') })]),
    };

    [Fact(DisplayName = "FLEET-04 a node killed between intent and done for an outward step: zero repeated outward actions")]
    public async Task Fleet04()
    {
        await using var f = await FleetRig.StartAsync();
        var a = await f.EnrollAsync("echo11", Scrape);
        var b = await f.EnrollAsync("echo12", Scrape);
        var sent = new Dictionary<string, int>(StringComparer.Ordinal);
        int Sent(string job) => sent.TryGetValue(job, out var n) ? n : 0;

        // Killed right after the outward action, before done: the job is lost and nobody sends again.
        var crash = f.Approved(Outward("deploy/crash", "prep", "send!"));
        var killer = new ScriptedRunner(s =>
        {
            if (s.StepId == "send") { sent[s.JobId] = Sent(s.JobId) + 1; throw new IOException("power cut"); }
            return Task.FromResult(new StepOutcome(true));
        });
        await Assert.ThrowsAsync<IOException>(() => f.Agent(a, killer).RunOnceAsync());
        f.Clock.Advance(TimeSpan.FromSeconds(61));
        f.Fleet.Tick();
        Assert.Equal("lost", f.Fleet.Job(crash.Id)!.State);
        Assert.Contains(f.Notices.Unseen(), n => n.Level == "alarm" && n.Text.Contains(crash.Id, StringComparison.Ordinal));
        var honest = new ScriptedRunner(s => { if (s.Outward) sent[s.JobId] = Sent(s.JobId) + 1; return Task.FromResult(new StepOutcome(true, ExternalId: "ext-" + s.StepId)); });
        Assert.Null(await f.Agent(b, honest).RunOnceAsync());
        Assert.Equal(1, Sent(crash.Id));

        // Killed after the outward step finished but before the report: the retry skips the finished step.
        var late = f.Approved(Outward("deploy/late", "send!", "verify"));
        var dies = new ScriptedRunner(s =>
        {
            if (s.StepId == "send") { sent[s.JobId] = Sent(s.JobId) + 1; return Task.FromResult(new StepOutcome(true, ExternalId: "sync-123")); }
            throw new IOException("power cut");
        });
        await Assert.ThrowsAsync<IOException>(() => f.Agent(a, dies).RunOnceAsync());
        f.Clock.Advance(TimeSpan.FromSeconds(61));
        f.Fleet.Tick();
        Assert.Equal("queued", f.Fleet.Job(late.Id)!.State);
        Assert.Equal(late.Id, await f.Agent(b, honest).RunOnceAsync());
        Assert.Equal("done", f.Fleet.Job(late.Id)!.State);
        Assert.Equal(1, Sent(late.Id));

        // Killed after the intent and before the action: still lost, and still never repeated blindly.
        var before = f.Approved(Outward("deploy/before", "send!"));
        var early = new ScriptedRunner(_ => throw new IOException("power cut"));
        await Assert.ThrowsAsync<IOException>(() => f.Agent(b, early).RunOnceAsync());
        f.Clock.Advance(TimeSpan.FromSeconds(61));
        f.Fleet.Tick();
        Assert.Equal("lost", f.Fleet.Job(before.Id)!.State);
        Assert.Equal(0, Sent(before.Id));

        // A node that comes back on a live lease cannot start the outward step again either.
        var split = f.Approved(Outward("deploy/split", "send!"));
        var na = f.Fleet.Node("echo11")!;
        var lease = f.Fleet.Lease(na)!;
        Assert.True(f.Fleet.Step(na, split.Id, "send", "intent", lease.Fencing, null).Ok);
        Assert.Equal(409, f.Fleet.Step(na, split.Id, "send", "intent", lease.Fencing, null).Status);
        Assert.Equal("lost", f.Fleet.Job(split.Id)!.State);
        Assert.Equal(1L, Convert.ToInt64(f.Db.Scalar("SELECT COUNT(*) FROM audit WHERE action='fleet.outward_repeat_refused';")));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => f.Db.Exec("DELETE FROM fleet_ledger;"));
    }

    [Fact(DisplayName = "FLEET-05 job selectors: never on a never-touch node, a node it names in never_nodes, or a node missing its labels")]
    public async Task Fleet05()
    {
        await using var f = await FleetRig.StartAsync();
        await f.EnrollAsync("render1", new Dictionary<string, string> { ["role"] = "render" });
        await f.EnrollAsync("scrape1", Scrape);
        await f.EnrollAsync("pandora", Scrape, neverTouch: true);
        var render = f.Fleet.Node("render1")!;
        var scrape = f.Fleet.Node("scrape1")!;
        var pandora = f.Fleet.Node("pandora")!;
        var sel = new JsonObject { ["selector"] = new JsonObject { ["labels"] = new JsonObject { ["role"] = "scrape" } } };

        var avoid = f.ReadOnly("q/avoid", new JsonObject { ["selector"] = sel["selector"]!.DeepClone(), ["never_nodes"] = new JsonArray("scrape1") });
        Assert.Null(f.Fleet.Lease(scrape));
        Assert.Null(f.Fleet.Lease(pandora));
        Assert.Null(f.Fleet.Lease(render));
        Assert.Equal("queued", f.Fleet.Job(avoid.Id)!.State);
        f.Fleet.Cancel(avoid.Id, "person:spencer");

        var wanted = f.ReadOnly("q/scrape", sel);
        Assert.Null(f.Fleet.Lease(render));
        Assert.Null(f.Fleet.Lease(pandora));
        Assert.Equal(wanted.Id, f.Fleet.Lease(scrape)!.JobId);

        var pinned = f.ReadOnly("q/pinned", new JsonObject { ["target_node"] = "render1" });
        Assert.Null(f.Fleet.Lease(pandora));
        Assert.Equal(pinned.Id, f.Fleet.Lease(render)!.JobId);
        Assert.Throws<FleetException>(() => f.ReadOnly("q/sneak", new JsonObject { ["allow_never_touch"] = true }));
        Assert.Throws<FleetException>(() => f.Fleet.AddJob(Outward("ro/outward", "send!").Also(j => j["kind"] = "readonly_query"), "person:spencer"));
    }

    [Fact(DisplayName = "FLEET-06 approved_by from a job or a node is ignored; the controller writes it from the person's approval record, bound to the job hash")]
    public async Task Fleet06()
    {
        await using var f = await FleetRig.StartAsync();
        await f.EnrollAsync("echo11", Scrape);
        var node = f.Fleet.Node("echo11")!;
        var spec = Outward("deploy/x", "go");
        spec["approved_by"] = "head-honcho";
        var job = f.Fleet.AddJob(spec, "person:spencer");
        Assert.Null(job.Spec["approved_by"]);
        Assert.Null(job.ApprovedBy);
        Assert.Equal("waiting_approval", job.State);
        Assert.Single(f.Db.Query("SELECT id FROM audit WHERE action='fleet.approved_by_ignored';", q => q.GetInt64(0)));
        Assert.Null(f.Fleet.Lease(node));

        var other = f.Fleet.AddJob(Outward("deploy/y", "go"), "person:spencer");
        var store = new ApprovalStore(f.Db, f.Clock, Redactor.Shared);
        var card = store.Get(job.ApprovalId!)!;
        Assert.True(card.PersonOnly);
        var policy = new ApproverPolicy(f.Db, f.Clock, f.R.Config);
        Assert.False(store.Decide(card.Id, Approver.Bot("head-honcho"), true, null, card.ActionHash, policy).Ok);
        Assert.False(store.Decide(card.Id, Approver.Person("spencer"), true, null, store.Get(other.ApprovalId!)!.ActionHash, policy).Ok);
        Assert.True(store.Decide(card.Id, Approver.Person("spencer"), true, null, card.ActionHash, policy).Ok);
        f.Fleet.Tick();
        Assert.Equal("person:spencer", f.Fleet.Job(job.Id)!.ApprovedBy);
        Assert.Equal("waiting_approval", f.Fleet.Job(other.Id)!.State);
        var lease = f.Fleet.Lease(node)!;
        Assert.Equal(job.Id, lease.JobId);
        Assert.Equal("person:spencer", lease.Spec["approved_by"]!.GetValue<string>());
        Assert.True(f.Fleet.Step(node, job.Id, "go", "intent", lease.Fencing, null).Ok);
        Assert.True(f.Fleet.Step(node, job.Id, "go", "done", lease.Fencing, null).Ok);
        Assert.True(f.Fleet.Complete(node, job.Id, lease.Fencing, new JsonObject { ["approved_by"] = "echo11", ["ok"] = true }).Ok);
        var stored = (string)f.Db.Scalar("SELECT result_json FROM fleet_jobs WHERE id=$i;", ("$i", job.Id))!;
        Assert.DoesNotContain("approved_by", stored);
        Assert.Equal("person:spencer", f.Fleet.Job(job.Id)!.ApprovedBy);
    }

    [Fact(DisplayName = "FLEET-07 a node's gateway token is refused by the inference server directly; only the gateway, holding the real key, gets through")]
    public async Task Fleet07()
    {
        await using var f = await FleetRig.StartAsync();
        const string realKey = "sk-real-inference-key-0123456789abcdef";
        SecureFile.EnsureDir(f.R.Paths.Secrets);
        SecureFile.WriteAllTextAtomic(f.R.Paths.InferenceKey, realKey + "\n");
        var id = await f.EnrollAsync("echo11", Scrape);
        await f.Agent(id, new ScriptedRunner(_ => Task.FromResult(new StepOutcome(true)))).RunOnceAsync();
        var nodeToken = File.ReadAllText(Path.Combine(f.Home("echo11"), "gateway.token")).Trim();
        Assert.True(SecureFile.IsPrivate(Path.Combine(f.Home("echo11"), "gateway.token")));
        Assert.NotEqual(realKey, nodeToken);
        foreach (var file in Directory.EnumerateFiles(f.Home("echo11"), "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(realKey, Encoding.Latin1.GetString(File.ReadAllBytes(file)));

        var upstream = new StubHandler((req, _) => Task.FromResult(req.Headers.Authorization?.ToString() == "Bearer " + realKey
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream") }
            : new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var direct = new HttpClient(upstream);
        using var directReq = new HttpRequestMessage(HttpMethod.Post, "https://relay.example.test/v1/chat/completions") { Content = new StringContent("{}") };
        directReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", nodeToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await direct.SendAsync(directReq)).StatusCode);

        var c = f.R.Config;
        c.Gateway.Slots = null;
        c.Gateway.NodeClasses.Add(new NodeClass { Name = "main-3090", MeasuredSlots = 1 });
        c.Gateway.UpstreamNodeClass = "main-3090";
        var tokens = new BotTokens(f.R.Paths, Redactor.Shared);
        var routes = new Dictionary<string, ModelRoute> { ["flash-next"] = new("flash-next", "Q", "", "", "", ["class=main-3090"]) };
        var gw = new PassThroughGateway(tokens.LoadHashes, () => routes, _ => realKey, new HttpClient(upstream), SystemClock.Instance, false, TimeSpan.FromSeconds(2))
        {
            Backends = new BackendRegistry(c, SystemClock.Instance),
            Contexts = new TurnContexts(f.Db, SystemClock.Instance, Priority.Assignment),
            Usage = new UsageMeter(f.Db, SystemClock.Instance),
        };
        DefaultHttpContext Ctx(string token)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Method = "POST";
            ctx.Request.Path = "/v1/chat/completions";
            ctx.Request.Headers.Authorization = "Bearer " + token;
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"model":"flash-next","stream":true,"messages":[]}"""));
            ctx.Response.Body = new MemoryStream();
            return ctx;
        }
        var through = Ctx(nodeToken);
        await gw.HandleAsync(through);
        Assert.Equal(200, through.Response.StatusCode);
        Assert.Contains(upstream.Seen, s => s.Auth == "Bearer " + realKey);
        Assert.Single(upstream.Seen, s => s.Auth == "Bearer " + nodeToken);

        f.Fleet.Revoke("echo11", "person:spencer");
        var refused = Ctx(nodeToken);
        await gw.HandleAsync(refused);
        Assert.Equal(401, refused.Response.StatusCode);
        Assert.Throws<FleetException>(() => f.ReadOnly("q/key", new JsonObject { ["secrets"] = new JsonArray("../inference.key") }));
    }

    [Fact(DisplayName = "FLEET-08 a per-job secret reaches only the lease holder as a 0600 file and never appears in process arguments, logs, audit lines, results or memory")]
    public async Task Fleet08()
    {
        await using var f = await FleetRig.StartAsync();
        const string secret = "SYNC-KEY-7f3a9c2e5b1d4f6a8c0e2b4d6f8a1c3e";
        var set = new CliEnv { Paths = f.R.Paths, AppDir = f.R.AppDir, Clock = f.R.Clock, Out = new StringWriter(), Err = new StringWriter(), ReadStdin = () => secret + "\n" };
        Assert.Equal(0, await new CliApp(set).RunAsync(["fleet", "secret", "set", "sync-api-key"]));
        Assert.DoesNotContain(secret, set.Out.ToString());
        var a = await f.EnrollAsync("echo11", Scrape);
        var b = await f.EnrollAsync("echo12", Scrape);
        var job = f.ReadOnly("q/secret", new JsonObject { ["secrets"] = new JsonArray("sync-api-key") });
        Assert.DoesNotContain(secret, job.Spec.ToJsonString());

        var nb = f.Fleet.Node("echo12")!;
        Assert.Equal(409, f.Fleet.Secrets(nb, job.Id, 1).Status);
        string? seenDir = null;
        var runner = new ScriptedRunner(s =>
        {
            seenDir = s.SecretsDir;
            var file = Path.Combine(s.SecretsDir!, "sync-api-key");
            Assert.True(SecureFile.IsPrivate(file));
            Assert.Equal(secret, File.ReadAllText(file));
            Assert.DoesNotContain(secret, s.Spec.ToJsonString());
            return Task.FromResult(new StepOutcome(true, Text: "used the key " + File.ReadAllText(file) + " once"));
        });
        Assert.Equal(job.Id, await f.Agent(a, runner).RunOnceAsync());
        Assert.NotNull(seenDir);
        Assert.False(Directory.Exists(seenDir));
        Assert.Equal("done", f.Fleet.Job(job.Id)!.State);

        var places = new[] { f.R.Paths.Home, f.Home("echo11"), f.Home("echo12") }.SelectMany(d => Directory.Exists(d) ? Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories) : [])
            .Where(p => !p.StartsWith(FleetController.JobSecretDir(f.R.Paths), StringComparison.Ordinal));
        foreach (var file in places)
            Assert.False(Encoding.Latin1.GetString(File.ReadAllBytes(file)).Contains(secret, StringComparison.Ordinal), file);
        var audit = string.Join("\n", f.Db.Query("SELECT action || ' ' || subject || ' ' || detail FROM audit;", q => q.GetString(0)));
        Assert.Contains("fleet.secret_read", audit);
        Assert.Contains("sync-api-key", audit);
        Assert.DoesNotContain(secret, audit);
        Assert.DoesNotContain(secret, (string)f.Db.Scalar("SELECT result_json FROM fleet_jobs WHERE id=$i;", ("$i", job.Id))!);
    }

    [Fact(DisplayName = "FLEET live: two enrolled nodes run read-only jobs over the FRPS and FRPC relay", Skip = "Needs two fleet nodes, the FRPS and FRPC relay with one remote port per node, and Spencer's direction (plan 7.21, task V3). In-process runs above use real mutual TLS on loopback.")]
    public void FleetLive() { }

    [Fact(DisplayName = "FLEET F7: signed rollout with a canary and rollback", Skip = "Not built: needs Spencer's release key, real nodes and the verify playbook per role (plan phase 12 F7). Recorded as an open item.")]
    public void FleetRollout() { }

    [Fact(DisplayName = "FLEET F9: all nodes and a 7 day soak", Skip = "Needs the fleet hardware and a week on the roster host (plan phase 12 F9).")]
    public void FleetSoak() { }
}

internal static class JsonExtensions
{
    public static JsonObject Also(this JsonObject o, Action<JsonObject> change)
    {
        change(o);
        return o;
    }
}
