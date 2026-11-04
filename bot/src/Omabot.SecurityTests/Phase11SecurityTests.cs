// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Omabot.Acp;
using Omabot.Approvals;
using Omabot.Cli;
using Omabot.Common;
using Omabot.Config;
using Omabot.Containers;
using Omabot.Db;
using Omabot.Egress;
using Omabot.Release;
using Omabot.Render;
using Omabot.Runtime;
using Omabot.Secrets;
using Omabot.Supervision;
using Omabot.Tests.Support;
using Omabot.Web;

namespace Omabot.SecurityTests;

/// <summary>Phase 11: web-multi user rosters and the security release gate (SEC-14, SEC-20 to SEC-25, SEC-27 to SEC-31 for two users, SEC-33).</summary>
public sealed class Phase11SecurityTests
{
    private static CliEnv Env(TempRoster r, Func<ISupervisorClient?>? sup = null) => new()
    {
        Paths = r.Paths, AppDir = r.AppDir, Clock = r.Clock, Out = new StringWriter(), Err = new StringWriter(), Supervisor = sup ?? (() => null),
    };

    [Fact(DisplayName = "SEC-14 image digest change: no bot starts until a person accepts the agent image, and a changed image is refused again")]
    public async Task Sec14()
    {
        using var r = new TempRoster();
        r.Config.Images.AcceptedDigest = null;
        r.Config.Save(r.Paths);
        await using var x = await Roster.StartAsync(r);
        var engine = new FakeContainerEngine();
        var sup = new DirectSupervisor(engine, r.Config.Bots.Network);
        BotLauncher Launcher(RosterConfig c) => new(x.Db, c, r.Paths, r.Clock, r.Audit(x.Db), x.Notices, sup,
            port => new AcpClient(new HttpClient(), x.Servers[x.Bots.All().Single(b => b.Port == port).Slug].BaseUrl), new LaunchOptions(r.AppDir, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10)));

        var refused = await Launcher(r.Config).UpAsync("builder");
        Assert.False(refused.Ok);
        Assert.Contains("No agent image is accepted", refused.Message);
        Assert.Empty(await engine.ListAsync("oma-bot.roster=1"));
        Assert.Equal(1, await new CliApp(Env(r, () => sup)).RunAsync(["images"]));

        Assert.Equal(1, await new CliApp(Env(r, () => sup)).RunAsync(["images", "accept", "--digest", "sha256:other"]));
        Assert.Null(RosterConfig.Load(r.Paths).Images.AcceptedDigest);
        Assert.Equal(0, await new CliApp(Env(r, () => sup)).RunAsync(["images", "accept"]));
        var accepted = RosterConfig.Load(r.Paths);
        Assert.Equal("sha256:fake", accepted.Images.AcceptedDigest);
        Assert.True((await Launcher(accepted).UpAsync("builder")).Ok);

        engine.ImageId = "sha256:changed";
        var changed = await Launcher(accepted).UpAsync("scout");
        Assert.False(changed.Ok);
        Assert.Contains("changed", changed.Message);
        var doctor = Env(r, () => sup);
        Assert.Equal(1, await new CliApp(doctor).RunAsync(["doctor"]));
        Assert.Contains("agent image", doctor.Out.ToString());
        Assert.Contains(x.Db.Query("SELECT action FROM audit;", q => q.GetString(0)), a => a == "images.accept");
        Assert.Contains(x.Db.Query("SELECT action FROM audit;", q => q.GetString(0)), a => a == "bot.up_refused");
    }

    [Fact(DisplayName = "SEC-20 cross-user access in web-multi: every read or write of another user's bots, cards, memory, rooms and audit is refused")]
    public async Task Sec20()
    {
        await using var rig = await WebRig.StartAsync(multi: true);
        Assert.Empty(WebMode.Problems(rig.R.Config, rig.R.Paths));
        var users = new UsersDb(rig.R.Paths.UsersDb);
        var alice = Tenants.Find(users, rig.R.Paths, "alice")!;
        var bob = Tenants.Find(users, rig.R.Paths, "bob")!;
        RosterDb Db(TenantHome h) => new(h.Paths.Db);
        ApprovalRow Card(TenantHome h, string bot) => new ApprovalStore(Db(h), rig.R.Clock, Redactor.Shared).Create(new ApprovalRequest(ApprovalKinds.Permission, bot,
            $"{bot} wants to run Bash: ls", new JsonObject { ["command"] = "ls" }, "bot", bot, SessionId: "ses_" + bot, PauseId: "perm_" + Guid.NewGuid().ToString("N")[..8], Ttl: TimeSpan.FromMinutes(10), Action: "ls"));
        var aliceCard = Card(alice, "alpha");
        var bobCard = Card(bob, "bravo");
        new AuditLog(Db(bob), rig.R.Clock, Redactor.Shared).Write("roster", "bob.private", "bravo", "only bob may read this");

        using var http = rig.Http();
        var a = (await rig.Login(http, "alice")).Cookie!;
        var csrf = await rig.Csrf(http, a);
        async Task<JsonNode?> Get(string path, string cookie) => await (await http.SendAsync(rig.Req(HttpMethod.Get, path, cookie: cookie))).Content.ReadFromJsonAsync<JsonNode>();
        async Task<HttpStatusCode> Status(HttpMethod m, string path, string cookie, object? body = null, string? token = null) =>
            (await http.SendAsync(rig.Req(m, path, body, cookie, token))).StatusCode;

        var bots = (await Get("/api/bots", a))!.AsArray();
        Assert.Equal(new[] { "Alpha" }, bots.Select(b => b!["name"]!.GetValue<string>()).ToArray());
        var cards = (await Get("/api/approvals", a))!.AsArray();
        Assert.Equal(new[] { aliceCard.Id }, cards.Select(c => c!["id"]!.GetValue<string>()).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, await Status(HttpMethod.Post, "/api/approvals/" + bobCard.Id, a, new { decision = "allow", hash = bobCard.ActionHash }, csrf));
        Assert.Equal("pending", new ApprovalStore(Db(bob), rig.R.Clock, Redactor.Shared).Get(bobCard.Id)!.Status);
        Assert.Equal(HttpStatusCode.NotFound, await Status(HttpMethod.Get, "/api/memory/bravo", a));
        Assert.Equal(HttpStatusCode.NotFound, await Status(HttpMethod.Post, "/api/memory/bravo/undo", a, new { id = 1 }, csrf));
        Assert.Equal(HttpStatusCode.OK, await Status(HttpMethod.Get, "/api/memory/alpha", a));
        Assert.Equal(new[] { "alpha-room" }, (await Get("/api/rooms", a))!.AsArray().Select(x => x!["name"]!.GetValue<string>()).ToArray());
        Assert.Equal(HttpStatusCode.NotFound, await Status(HttpMethod.Get, "/api/rooms/bravo-room", a));
        Assert.Equal(HttpStatusCode.NotFound, await Status(HttpMethod.Post, "/api/rooms/bravo-room", a, new { text = "hi" }, csrf));
        Assert.Equal(0L, Convert.ToInt64(Db(bob).Scalar("SELECT COUNT(*) FROM room_messages;")));
        var audit = (await Get("/api/audit?n=500", a))!.ToJsonString();
        Assert.DoesNotContain("bob.private", audit);
        Assert.DoesNotContain(bobCard.Id, audit);
        // A header or query that names another roster changes nothing.
        var probe = rig.Req(HttpMethod.Get, "/api/bots?home=bob&user=bob", cookie: a);
        probe.Headers.Add("X-Roster", "bob");
        var probed = await (await http.SendAsync(probe)).Content.ReadFromJsonAsync<JsonArray>();
        Assert.Equal(new[] { "Alpha" }, probed!.Select(b => b!["name"]!.GetValue<string>()).ToArray());

        Assert.Equal(HttpStatusCode.OK, await Status(HttpMethod.Post, "/api/approvals/" + aliceCard.Id, a, new { decision = "deny", hash = aliceCard.ActionHash }, csrf));

        var b = (await rig.Login(http, "bob")).Cookie!;
        Assert.Equal(new[] { "Bravo" }, (await Get("/api/bots", b))!.AsArray().Select(x => x!["name"]!.GetValue<string>()).ToArray());
        Assert.Contains("bob.private", (await Get("/api/audit?n=500", b))!.ToJsonString());

        var c = (await rig.Login(http, "carol")).Cookie!;
        var carolCsrf = await rig.Csrf(http, c);
        Assert.Equal(new[] { "Alpha" }, (await Get("/api/bots", c))!.AsArray().Select(x => x!["name"]!.GetValue<string>()).ToArray());
        Assert.Equal(HttpStatusCode.Forbidden, await Status(HttpMethod.Get, "/api/audit", c));
        var second = Card(alice, "alpha");
        Assert.Equal(HttpStatusCode.Forbidden, await Status(HttpMethod.Post, "/api/approvals/" + second.Id, c, new { decision = "allow", hash = second.ActionHash }, carolCsrf));

        var s = (await rig.Login(http, "spencer")).Cookie!;
        Assert.Equal(HttpStatusCode.Forbidden, await Status(HttpMethod.Get, "/api/bots", s));

        File.Delete(Path.Combine(rig.R.Paths.Web, "release-signoff.md"));
        Assert.Contains(WebMode.Problems(rig.R.Config, rig.R.Paths), p => p.Contains("outside review", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "SEC-21 two users: separate names, networks, subnets, bridges, firewall chains, ports, gateways, tokens and workspaces; each supervisor sees and changes only its own containers")]
    public async Task Sec21TwoUsers()
    {
        using var host = new TempRoster();
        var a = Tenants.ConfigFor(host.Config, host.Paths, "alice", 1);
        var b = Tenants.ConfigFor(host.Config, host.Paths, "bob", 2);
        var aNets = Networks.All(a);
        var bNets = Networks.All(b);
        foreach (var n in aNets)
        {
            Assert.DoesNotContain(bNets, m => m.Name == n.Name || m.Bridge == n.Bridge);
            Assert.DoesNotContain(bNets.Concat(Networks.All(host.Config)), m => Networks.Contains(m.Subnet, Networks.Parse(n.Subnet).Network));
        }
        Assert.NotEqual(a.Gateway.BindAddress, b.Gateway.BindAddress);
        var aPorts = new[] { a.Api.Port, a.Gateway.Port }.Concat(Enumerable.Range(a.Bots.PortBase, a.Bots.PortCount)).Concat(Enumerable.Range(a.Desktops.PortBase, a.Desktops.Max)).ToHashSet();
        var bPorts = new[] { b.Api.Port, b.Gateway.Port }.Concat(Enumerable.Range(b.Bots.PortBase, b.Bots.PortCount)).Concat(Enumerable.Range(b.Desktops.PortBase, b.Desktops.Max)).ToHashSet();
        Assert.Empty(aPorts.Intersect(bPorts));
        Assert.DoesNotContain(host.Config.Api.Port, aPorts);
        Assert.NotEqual(FirewallRules.UserChainFor(a), FirewallRules.UserChainFor(b));
        Assert.DoesNotContain(FirewallRules.Rules(a), l => l.Contains("u02", StringComparison.OrdinalIgnoreCase) || l.Contains("OMABOT-USER ", StringComparison.Ordinal));
        Assert.Contains("OMABOT-U01-USER", FirewallRules.Script(a));
        Assert.NotEqual(a.WorkspaceRoots.Single(), b.WorkspaceRoots.Single());
        Assert.Equal("local", a.Mode);
        Assert.False(a.HostExec.OpenFirewall);
        var open = a.Clone();
        open.HostExec.OpenFirewall = true;
        Assert.Throws<InvalidDataException>(open.Validate);

        using var ra = new TempRoster(tenant: 1);
        using var rb = new TempRoster(tenant: 2);
        Assert.Equal(0, await new CliApp(Env(ra)).RunAsync(["team", "apply", ra.TeamFile(TempRoster.TwoBots)]));
        Assert.Equal(0, await new CliApp(Env(rb)).RunAsync(["team", "apply", rb.TeamFile(TempRoster.TwoBots)]));
        var hostExec = Env(ra);
        Assert.Equal(Exit.Invalid, await new CliApp(hostExec).RunAsync(["team", "apply", ra.TeamFile(TempRoster.TwoBots.Replace("permission_profile: edits", "permission_profile: edits\n    host_exec: true"))]));
        Assert.Contains("not available in a web-multi user roster", hostExec.Err.ToString());
        var ta = new BotTokens(ra.Paths, Redactor.Shared).Ensure("builder");
        var tb = new BotTokens(rb.Paths, Redactor.Shared).Ensure("builder");
        Assert.NotEqual(ta, tb);
        Assert.DoesNotContain(SecureFile.Sha256Hex(tb), new BotTokens(ra.Paths, Redactor.Shared).LoadHashes().Values);

        var engine = new FakeContainerEngine();
        var supA = new DirectSupervisor(engine, ra.Config.Bots.Network, tenant: "u01");
        var supB = new DirectSupervisor(engine, rb.Config.Bots.Network, tenant: "u02");
        var supHost = new DirectSupervisor(engine, host.Config.Bots.Network);
        async Task<ContainerSpec> Spec(TempRoster r)
        {
            var row = new BotStore(r.Db(), r.Clock).Get("builder");
            return ContainerSpec.For(row.Spec, row.Port, new BotTokens(r.Paths, Redactor.Shared).Ensure("builder"), r.Paths, r.Config, new BotRenderer(r.Paths, r.Config), r.AppDir, false);
        }
        var specA = await Spec(ra);
        var specB = await Spec(rb);
        Assert.Equal(specA.Name, specB.Name);
        await supA.EnsureRunningAsync(specA);
        await supB.EnsureRunningAsync(specB);
        Assert.NotNull(await engine.InspectAsync("u01-" + specA.Name));
        Assert.NotNull(await engine.InspectAsync("u02-" + specB.Name));
        var mine = Assert.Single(await supA.ListAsync());
        Assert.Equal(specA.Name, mine.Name);
        Assert.Equal("u01", mine.Labels[DirectSupervisor.TenantLabel]);
        Assert.Single(await supB.ListAsync());
        Assert.Empty(await supHost.ListAsync());
        await supA.StopAsync(specA.Name);
        Assert.False((await engine.InspectAsync("u01-" + specA.Name))!.Running);
        Assert.True((await engine.InspectAsync("u02-" + specB.Name))!.Running);
        await Assert.ThrowsAsync<ContainerEngineException>(() => supA.StopAsync("u02-" + specB.Name));

        var squatter = new FakeContainerEngine();
        await squatter.CreateAsync(specA with { Name = "u01-" + specA.Name });
        await Assert.ThrowsAsync<ContainerEngineException>(() => new DirectSupervisor(squatter, ra.Config.Bots.Network, tenant: "u01").EnsureRunningAsync(specA));
    }

    [Fact(DisplayName = "SEC-22 two users: every user's bot container keeps the hardening (no privileged mode, capabilities dropped, no-new-privileges, non-root, limits)")]
    public async Task Sec22TwoUsers()
    {
        foreach (var slot in new[] { 1, 2 })
        {
            using var r = new TempRoster(tenant: slot);
            Assert.Equal(0, await new CliApp(Env(r)).RunAsync(["team", "apply", r.TeamFile(TempRoster.TwoBots)]));
            foreach (var row in new BotStore(r.Db(), r.Clock).All())
            {
                var spec = ContainerSpec.For(row.Spec, row.Port, "omb_t" + new string('q', 30), r.Paths, r.Config, new BotRenderer(r.Paths, r.Config), r.AppDir, false);
                Assert.Empty(spec.SafetyProblems());
                var json = spec.ToEngineJson().ToJsonString();
                Assert.Contains("\"CapDrop\":[\"ALL\"]", json);
                Assert.Contains("no-new-privileges", json);
                Assert.DoesNotContain("\"Privileged\":true", json);
                Assert.DoesNotContain("docker.sock", json);
                Assert.StartsWith("1000", spec.User);
                Assert.Contains(r.Config.Bots.Network, json);
            }
        }
    }

    [Fact(DisplayName = "SEC-25 a known critical advisory against a pinned package blocks the release; so do a loose version and missing advisory data")]
    public void Sec25()
    {
        var bot = Path.Combine(Applied.RepoRoot(), "bot");
        var pinned = ReleaseGate.Packages(bot).First(p => p.Package == "YamlDotNet");
        var critical = ReleaseGate.Check(bot, [new Advisory("YamlDotNet", [pinned.Version], "critical", "TEST-1")], null, false);
        var line = Assert.Single(critical, l => l.Check == "advisories");
        Assert.False(line.Ok);
        Assert.Contains("YamlDotNet", line.Message);
        Assert.True(Assert.Single(ReleaseGate.Check(bot, [new Advisory("YamlDotNet", ["0.0.1"], "critical", "TEST-2")], null, false), l => l.Check == "advisories").Ok);
        Assert.True(Assert.Single(ReleaseGate.Check(bot, [new Advisory("YamlDotNet", [pinned.Version], "moderate", "TEST-3")], null, false), l => l.Check == "advisories").Ok);
        Assert.False(Assert.Single(ReleaseGate.Check(bot, null, null, false), l => l.Check == "advisories").Ok);
        Assert.True(Assert.Single(critical, l => l.Check == "dash").Ok);
        Assert.True(Assert.Single(critical, l => l.Check == "secrets").Ok);
        Assert.True(Assert.Single(critical, l => l.Check == "pins").Ok);
        Assert.False(Assert.Single(critical, l => l.Check == "signoff").Ok);

        using var r = new TempRoster();
        var fake = Path.Combine(r.Root, "bot");
        Directory.CreateDirectory(Path.Combine(fake, "docs"));
        File.WriteAllText(Path.Combine(fake, "README.md"), "readme");
        File.WriteAllText(Path.Combine(fake, "a.csproj"), "<Project><ItemGroup><PackageReference Include=\"Some.Lib\" Version=\"1.*\" /></ItemGroup></Project>");
        Assert.False(Assert.Single(ReleaseGate.Check(fake, [], null, false), l => l.Check == "pins").Ok);
        var signoff = Path.Combine(fake, "docs", "signoff.md");
        File.WriteAllText(signoff, "version: 1.0.0\ncommit: abc1234\nsecurity_gate: passed\nsigned_by: Spencer\ndate: 2026-11-04\n");
        Assert.True(ReleaseGate.Signoff(signoff, multi: false).Ok);
        Assert.False(ReleaseGate.Signoff(signoff, multi: true).Ok);
        File.AppendAllText(signoff, "outside_review: done\n");
        Assert.True(ReleaseGate.Signoff(signoff, multi: true).Ok);
    }

    private const string ApprovalTeam = """
        version: 1
        models:
          flash-next:
            upstream_model: Qwen3.8-Flash-Next
        approvers:
          - { bot: Reviewer, kinds: [permission] }
        bots:
          - name: Builder
            personality: You are Builder.
            model: flash-next
            permission_profile: edits
            tools: { allow: [FileRead, FileWrite, Bash, mcp__roster__approval_answer] }
          - name: Reviewer
            personality: You review requests.
            model: flash-next
            tools: { allow: [FileRead, mcp__roster__approval_answer] }
        """;

    [Fact(DisplayName = "SEC-27 to SEC-31 for two users: in each user roster a bot cannot approve its own pause, answers bind to their card, and one user's approver cannot reach the other's cards")]
    public async Task NoSelfApprovalTwoUsers()
    {
        var rosters = new List<(TempRoster R, Roster X, ApprovalRow Card)>();
        try
        {
            foreach (var slot in new[] { 1, 2 })
            {
                var r = new TempRoster(tenant: slot);
                var x = await Roster.StartAsync(r, ApprovalTeam, broker: true);
                var pause = "perm_u" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
                x.Behavior["builder"] = _ => [("text_delta", new { content = "asking" }), ("permission_request", new { id = pause, tool = "Bash", summary = "dotnet test", dangerous = false })];
                await x.PersonAsync("builder", "run the tests");
                rosters.Add((r, x, Assert.Single(x.Runtime.Approvals.Pending())));
            }
            foreach (var (r, x, card) in rosters)
            {
                var own = x.Call("builder", "approval_answer", new JsonObject { ["id"] = card.Id, ["decision"] = "allow" });
                Assert.False(own.Ok);
                Assert.Equal(ApprovalStatus.Pending, x.Runtime.Approvals.Get(card.Id)!.Status);
                var forged = x.Runtime.Approvals.Decide(card.Id, Approver.Bot("builder"), true, "forged", card.ActionHash, new ApproverPolicy(x.Db, r.Clock, r.Config));
                Assert.False(forged.Ok);
                var wrongHash = x.Runtime.Approvals.Decide(card.Id, Approver.Person("spencer"), true, null, new string('0', 64), new ApproverPolicy(x.Db, r.Clock, r.Config));
                Assert.False(wrongHash.Ok);
            }
            var (r1, x1, _) = rosters[0];
            var (r2, x2, card2) = rosters[1];
            ApprovalRow Bound(Roster x, string boundTo) => x.Runtime.Approvals.Create(new ApprovalRequest(ApprovalKinds.Permission, "builder", "Builder wants to run Bash: ls",
                new JsonObject { ["command"] = "ls" }, "bot", "builder", SessionId: "ses_b", PauseId: "perm_" + Guid.NewGuid().ToString("N")[..6], Ttl: TimeSpan.FromMinutes(10), Action: "ls", BoundTo: boundTo));
            var mineToReview = Bound(x1, "bot:reviewer");
            var theirs = Bound(x2, "bot:reviewer");
            Assert.False(x1.Runtime.Approvals.Decide(theirs.Id, Approver.Bot("reviewer"), true, null, theirs.ActionHash, new ApproverPolicy(x1.Db, r1.Clock, r1.Config)).Ok);
            Assert.Equal(ApprovalStatus.Pending, x2.Runtime.Approvals.Get(theirs.Id)!.Status);
            Assert.False(x1.Call("reviewer", "approval_answer", new JsonObject { ["id"] = card2.Id, ["decision"] = "allow" }).Ok);
            Assert.Equal(ApprovalStatus.Pending, x2.Runtime.Approvals.Get(card2.Id)!.Status);
            Assert.True(x1.Runtime.Approvals.Decide(mineToReview.Id, Approver.Bot("reviewer"), true, null, mineToReview.ActionHash, new ApproverPolicy(x1.Db, r1.Clock, r1.Config)).Ok);
            var personBound = Bound(x2, "person:alice");
            Assert.False(x2.Runtime.Approvals.Decide(personBound.Id, Approver.Person("bob"), true, null, personBound.ActionHash, new ApproverPolicy(x2.Db, r2.Clock, r2.Config)).Ok);

            var session = x2.Servers["builder"].Sessions.Values.Single(s => s.PendingPause == "perm_u2");
            Assert.True(x2.Servers["builder"].AnswerFromInside(session.Id, "{\"permission\":{\"id\":\"perm_u2\",\"decision\":\"allow\"}}"));
            Assert.True(x2.PersonAnswer(card2.Id, true).Ok);
            await x2.BrokerTickAsync();
            Assert.False(x2.Bots.Get("builder").Enabled);
            Assert.True(x1.Bots.Get("builder").Enabled);
            Assert.Contains(x2.Notices.Unseen(), n => n.Level == "alarm");
            Assert.DoesNotContain(x1.Notices.Unseen(), n => n.Level == "alarm");
        }
        finally
        {
            foreach (var (r, x, _) in rosters)
            {
                await x.DisposeAsync();
                r.Dispose();
            }
        }
    }

    [Fact(DisplayName = "SEC-33 a bot has no way to run a fleet command or start a container: no such tool, refused and audited; docker and oma-bot never enter a bot's commands")]
    public async Task Sec33()
    {
        using var r = new TempRoster();
        var db = await Applied.DefaultPlusTwoAsync(r);
        var bots = new BotStore(db, r.Clock);
        var services = new RosterServices(db, r.Clock, r.Paths, null);
        services.Tools.Audit = r.Audit(db);
        foreach (var bot in bots.All())
            Assert.DoesNotContain(services.Tools.ListJson(bot.Slug), t => t!["name"]!.GetValue<string>() is var n && (n.Contains("fleet", StringComparison.Ordinal) || n.Contains("container", StringComparison.Ordinal) || n.Contains("docker", StringComparison.Ordinal)));
        foreach (var name in new[] { "fleet_apply", "fleet_invite", "container_start", "docker_run" })
        {
            var res = await services.Tools.CallAsync(new Api.ToolCaller("builder", null, null), name, [], CancellationToken.None);
            Assert.Equal(404, res.Status);
        }
        var refusals = db.Query("SELECT subject FROM audit WHERE action='tool.refused';", q => q.GetString(0));
        Assert.Contains("fleet_apply", refusals);
        Assert.Contains("docker_run", refusals);
        foreach (var allow in new[] { "docker run *", "oma-bot fleet *", "sudo docker ps" })
        {
            var env = Env(r);
            Assert.Equal(Exit.Invalid, await new CliApp(env).RunAsync(["team", "apply", r.TeamFile(TempRoster.TwoBots.Replace("\"dotnet test*\"", $"\"{allow}\""))]));
            Assert.Contains("bash", env.Err.ToString());
        }
        Assert.Contains("docker", Team.ToolCatalog.NeverAutoBash);
        Assert.Contains("oma-bot", Team.ToolCatalog.NeverAutoBash);
    }

    [Fact(DisplayName = "SEC-32 relay leg: the inference key on the wire is checked against the fix chosen in task V4", Skip = "Needs Spencer's decision D20 (task V4) and the FRPS and FRPC relay; until then the gateway refuses plain http upstreams unless allowed by hand (decision B1).")]
    public void Sec32() { }
}
