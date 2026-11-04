// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using Omabot.Approvals;
using Omabot.Cli;
using Omabot.Common;
using Omabot.Db;
using Omabot.Gateway;
using Omabot.Routines;
using Omabot.Runtime;
using Omabot.Supervision;
using Omabot.Tests.Support;

namespace Omabot.Tests;

public sealed class HandoffUnitTests : IAsyncLifetime
{
    private readonly TempRoster _r = new();
    private Roster _x = null!;

    public async Task InitializeAsync() => _x = await Roster.StartAsync(_r);

    public async Task DisposeAsync()
    {
        await _x.DisposeAsync();
        _r.Dispose();
    }

    private AssignmentRuntime Rt => _x.Runtime;

    /// <summary>A running assignment for <paramref name="bot"/>, as if its turn were in progress.</summary>
    private AssignmentRow Running(string bot, string? parent = null, string brief = "start", string? requesterBot = null, string? turn = null)
    {
        var e = Rt.Enqueue(new EnqueueRequest(bot, "work", brief, ParentId: parent, Origin: "person", RequesterKind: parent is null ? "person" : "bot",
            RequesterBot: requesterBot, RequesterTurn: turn, Rework: true));
        Assert.True(e.Ok, e.Message);
        Assert.True(Rt.Store.Transition(e.Id!, [AssignmentStatus.Queued], AssignmentStatus.Running, "test", "admitted",
            new Dictionary<string, object?> { ["lease_owner"] = "turn_test_" + bot, ["started_at"] = AssignmentStore.Iso(_r.Clock.UtcNow) }));
        return Rt.Store.Get(e.Id!)!;
    }

    private EnqueueResult Child(AssignmentRow parent, string to, string brief, string mode = "work", bool rework = false, string? turn = "t1", string? level = null)
        => Rt.Enqueue(new EnqueueRequest(to, mode, brief, ParentId: parent.Id, RequesterKind: "bot", RequesterBot: parent.AssigneeBot, RequesterTurn: turn, Rework: rework, RequestedLevel: level));

    [Fact(DisplayName = "UNIT-HO-01 A to B to A is refused with reason ancestor")]
    public void Ho01()
    {
        var root = Running("scout");
        var toBuilder = Child(root, "Builder", "fix the build");
        Assert.True(toBuilder.Ok, toBuilder.Message);
        Rt.Store.Transition(toBuilder.Id!, [AssignmentStatus.Queued], AssignmentStatus.Running, "test", "admitted", new Dictionary<string, object?> { ["lease_owner"] = "x" });
        var back = Child(Rt.Store.Get(toBuilder.Id!)!, "Scout", "please check this for me");
        Assert.Equal((false, EnqueueReasons.Ancestor), (back.Ok, back.Reason));
        var self = Child(root, "Scout", "do it yourself");
        Assert.Equal(EnqueueReasons.Ancestor, self.Reason);
    }

    [Fact(DisplayName = "UNIT-HO-02 depth 4 is refused with reason depth")]
    public void Ho02()
    {
        var node = Running("lead");
        foreach (var bot in new[] { "scout", "builder", "editor" })
        {
            var e = Child(node, bot, "go one level down to " + bot);
            Assert.True(e.Ok, e.Message);
            Rt.Store.Transition(e.Id!, [AssignmentStatus.Queued], AssignmentStatus.Running, "test", "admitted", new Dictionary<string, object?> { ["lease_owner"] = "x" });
            node = Rt.Store.Get(e.Id!)!;
        }
        Assert.Equal(3, node.Depth);
        var tooDeep = Child(node, "Helper", "one more level");
        Assert.Equal((false, EnqueueReasons.Depth), (tooDeep.Ok, tooDeep.Reason));
    }

    [Fact(DisplayName = "UNIT-HO-03 the fifth child in one turn is refused; another turn may hand out more")]
    public void Ho03()
    {
        var root = Running("lead");
        for (var i = 0; i < 4; i++) Assert.True(Child(root, "Scout", $"look up topic number {i}", turn: "turnA").Ok);
        var fifth = Child(root, "Scout", "look up topic number 5", turn: "turnA");
        Assert.Equal((false, EnqueueReasons.Fanout), (fifth.Ok, fifth.Reason));
        Assert.True(Child(root, "Scout", "look up topic number 6", turn: "turnB").Ok);
    }

    [Fact(DisplayName = "UNIT-HO-04 the same normalized brief within 10 minutes joins; after the window it is new")]
    public void Ho04()
    {
        var root = Running("lead");
        var a = Child(root, "Scout", "Find the ACP pause enum.");
        var b = Child(root, "Scout", "  find the ACP   pause enum ", turn: "t2");
        Assert.Equal((EnqueueReasons.Joined, a.Id), (b.Reason, b.Id));
        var other = Child(root, "Scout", "Find the ACP pause enum, please", turn: "t2");
        Assert.NotEqual(a.Id, other.Id);
        _r.Clock.Advance(TimeSpan.FromMinutes(11));
        var later = Child(root, "Scout", "Find the ACP pause enum.", turn: "t3");
        Assert.NotEqual(a.Id, later.Id);
        Assert.Equal(EnqueueReasons.Ok, later.Reason);
    }

    [Fact(DisplayName = "UNIT-HO-05 rework bypasses dedupe")]
    public void Ho05()
    {
        var root = Running("lead");
        var a = Child(root, "Scout", "Find the ACP pause enum.");
        var b = Child(root, "Scout", "Find the ACP pause enum.", rework: true);
        Assert.True(b.Ok);
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact(DisplayName = "UNIT-HO-06 / SEC-04 a child's level is never above its requester's")]
    public void Ho06()
    {
        var personToBuilder = Running("builder");
        Assert.Equal("edits", personToBuilder.Level);
        var personToScout = Running("scout");
        Assert.Equal("ask", personToScout.Level);
        var scoutToBuilder = Child(personToScout, "Builder", "change the file");
        Assert.Equal("ask", Rt.Store.Get(scoutToBuilder.Id!)!.Level);
        var builderToEditor = Child(personToBuilder, "Editor", "fix the wording");
        Assert.Equal("edits", Rt.Store.Get(builderToEditor.Id!)!.Level);
        var above = Child(personToScout, "Builder", "change it at a higher level", level: "auto");
        Assert.Equal((false, EnqueueReasons.Level), (above.Ok, above.Reason));
        var routineRoot = Rt.Enqueue(new EnqueueRequest("builder", "work", "routine work", Origin: "routine", RequesterKind: "routine", Rework: true));
        Assert.Equal("ask", Rt.Store.Get(routineRoot.Id!)!.Level);
    }

    [Fact(DisplayName = "UNIT-HO-07 assignment_events is append-only")]
    public void Ho07()
    {
        var root = Running("lead");
        Assert.ThrowsAny<Exception>(() => _x.Db.Exec("UPDATE assignment_events SET reason='edited' WHERE assignment_id=$a;", ("$a", root.Id)));
        Assert.ThrowsAny<Exception>(() => _x.Db.Exec("DELETE FROM assignment_events WHERE assignment_id=$a;", ("$a", root.Id)));
        Assert.True(Rt.Store.Events(root.Id).Count >= 2);
    }

    [Fact(DisplayName = "UNIT-HO-08 a row and its event are written together or not at all")]
    public void Ho08()
    {
        var id = AssignmentStore.NewId();
        var template = Running("lead") with { Id = id, Status = AssignmentStatus.Queued };
        Assert.ThrowsAny<InvalidOperationException>(() => _x.Db.InTransaction((c, tx) =>
        {
            Rt.Store.Insert(c, tx, template, "test", "enqueued");
            throw new InvalidOperationException("crash between the row and the next step");
        }));
        Assert.Null(Rt.Store.Get(id));
        Assert.Empty(Rt.Store.Events(id));
        // A refused transition writes neither the change nor an event.
        var root = Running("scout");
        var before = Rt.Store.Events(root.Id).Count;
        Assert.False(Rt.Store.Transition(root.Id, [AssignmentStatus.Waiting], AssignmentStatus.Resume, "test", "wrong state"));
        Assert.Equal(before, Rt.Store.Events(root.Id).Count);
    }

    [Fact(DisplayName = "Enqueue refusals are typed: unknown bot, disabled bot, accepts_from, parent not running, bad input")]
    public void TypedRefusals()
    {
        var root = Running("builder");
        Assert.Equal(EnqueueReasons.NotFound, Child(root, "Nobody", "x").Reason);
        Assert.Equal(EnqueueReasons.Refused, Child(root, "Lead", "lead does not accept builder").Reason);
        _x.Bots.Disable("editor", "test");
        Assert.Equal(EnqueueReasons.Disabled, Child(root, "Editor", "x").Reason);
        var queued = Rt.Enqueue(new EnqueueRequest("scout", "work", "queued root", Rework: true));
        Assert.Equal(EnqueueReasons.ParentNotRunning, Rt.Enqueue(new EnqueueRequest("helper", "work", "x", ParentId: queued.Id, RequesterKind: "bot", RequesterBot: "scout")).Reason);
        Assert.Equal(EnqueueReasons.Invalid, Child(root, "Scout", "x", mode: "delete").Reason);
        Assert.Equal(EnqueueReasons.Invalid, Child(root, "Scout", "").Reason);
        Assert.Equal(EnqueueReasons.Invalid, Child(root, "Scout", new string('x', 9000)).Reason);
        Assert.Equal(EnqueueReasons.Invalid, Child(root, "Scout", new string('n', 5000), mode: "notify").Reason);
        Assert.Equal(EnqueueReasons.Invalid, Rt.Enqueue(new EnqueueRequest("scout", "work", "x", ParentId: root.Id, RequesterKind: "bot", RequesterBot: "editor")).Reason);
    }

    [Fact(DisplayName = "Routing: lead, keyword routes and names")]
    public void RoutingRules()
    {
        var scout = _x.Bots.Get("Scout");
        string? To(string to, string brief) => Routing.Resolve(to, brief, scout, _x.Db, _x.Bots).Bot?.Slug;
        Assert.Equal("lead", To("lead", "please triage this"));
        Assert.Equal("builder", To("auto", "the build is broken, run the test suite"));
        Assert.Equal("editor", To("auto", "tidy the docs wording please"));
        Assert.Equal("lead", To("auto", "something with no keyword at all"));
        Assert.Equal("helper", To("Helper", "anything"));
        Assert.Null(To("Nobody", "anything"));
        Assert.NotNull(Routing.Resolve("lead", "x", _x.Bots.Get("Lead"), _x.Db, _x.Bots).Problem);
        // The route picks the bot; the bot's accepts_from still decides: Lead takes work only from a person.
        var root = Running("scout");
        Assert.Equal(EnqueueReasons.Refused, Child(root, "lead", "please triage this").Reason);
        Assert.Equal("builder", Rt.Store.Get(Child(root, "auto", "the build is broken, run the test suite").Id!)!.AssigneeBot);
    }

    [Fact(DisplayName = "UNIT-INV-01 to 09 each broken invariant is reported by the checker")]
    public void InvariantChecker()
    {
        var db = _x.Db;
        string Flagged(string prefix) => Assert.Single(Invariants.Check(db, quiesced: false), v => v.StartsWith(prefix, StringComparison.Ordinal));
        Assert.Empty(Invariants.Check(db, quiesced: false));
        // I1: waiting with nothing to wait on.
        var w = Running("lead", brief: "i1");
        Rt.Store.Transition(w.Id, [AssignmentStatus.Running], AssignmentStatus.Waiting, "test", "waiting on nothing");
        Flagged("I1");
        Rt.Settle(Rt.Store.Get(w.Id)!, AssignmentStatus.Completed, Outcomes.Ok, null, "ok");
        Assert.Empty(Invariants.Check(db, quiesced: false));
        // I2: a settled child with no report.
        var p = Running("lead", brief: "i2");
        var c = Child(p, "Scout", "report me");
        Rt.Store.Transition(c.Id!, [AssignmentStatus.Queued], AssignmentStatus.Completed, "test", "settled without the runtime");
        Assert.Contains(Invariants.Check(db, false), v => v.StartsWith("I2", StringComparison.Ordinal));
        _x.Db.InTransaction((cn, tx) =>
        {
            Rt.Store.AddInbox(cn, tx, "lead", c.Id!, "child_done", new { });
            Rt.Store.Transition(cn, tx, c.Id!, [AssignmentStatus.Completed], AssignmentStatus.Completed, "test", "reported", new Dictionary<string, object?> { ["reported"] = 1 });
        });
        Rt.Settle(Rt.Store.Get(p.Id)!, AssignmentStatus.Completed, Outcomes.Ok, null, "ok");
        Assert.Empty(Invariants.Check(db, false));
        // I3: the same brief dispatched twice inside the window.
        var dup = Rt.Store.Get(Running("helper", brief: "dup").Id)!;
        _x.Db.InTransaction((cn, tx) => Rt.Store.Insert(cn, tx, dup with { Id = AssignmentStore.NewId(), Rework = false, Status = AssignmentStatus.Queued, CreatedAt = dup.CreatedAt.AddMinutes(1) }, "test", "forged"));
        _x.Db.Exec("UPDATE assignments SET rework=0 WHERE id=$i;", ("$i", dup.Id));
        Assert.Contains(Invariants.Check(db, false), v => v.StartsWith("I3", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "UNIT-INV-04 to 09 a root left live, expired completion, wrong level, an edit without an event, a second rerun, an odd wake")]
    public void InvariantChecker2()
    {
        var db = _x.Db;
        var root = Running("lead", brief: "quiet root");
        Assert.Contains(Invariants.Check(db, quiesced: true), v => v.StartsWith("I1", StringComparison.Ordinal) || v.StartsWith("I4", StringComparison.Ordinal));
        Rt.Settle(root, AssignmentStatus.Completed, Outcomes.Ok, null, "ok");
        Assert.Empty(Invariants.Check(db, true));
        var a = Running("scout", brief: "i5");
        Rt.Settle(a, AssignmentStatus.Completed, Outcomes.Expired, "forged", null);
        Assert.Contains(Invariants.Check(db, true), v => v.StartsWith("I5", StringComparison.Ordinal));
        var b = Running("builder", brief: "i6");
        Rt.Store.Update(b.Id, "roster", "dispatched", new Dictionary<string, object?>(), new { level = "auto" });
        Assert.Contains(Invariants.Check(db, false), v => v.StartsWith("I6", StringComparison.Ordinal));
        db.Exec("UPDATE assignments SET status='queued' WHERE id=$i;", ("$i", b.Id));
        Assert.Contains(Invariants.Check(db, false), v => v.StartsWith("I7", StringComparison.Ordinal));
        var c = Running("editor", brief: "i8");
        Rt.Store.Update(c.Id, "test", "forged", new Dictionary<string, object?> { ["restarts"] = 2 });
        Assert.Contains(Invariants.Check(db, false), v => v.StartsWith("I8", StringComparison.Ordinal));
        var d = Running("helper", brief: "i9");
        Rt.Store.Transition(d.Id, [AssignmentStatus.Running], AssignmentStatus.Waiting, "test", "waiting");
        Rt.Store.Transition(d.Id, [AssignmentStatus.Waiting], AssignmentStatus.Resume, "test", "felt like it");
        Assert.Contains(Invariants.Check(db, false), v => v.StartsWith("I9", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "UNIT-INV-10 the runtime never throws into a turn: bad input gives typed refusals")]
    public void Inv10()
    {
        var root = Running("lead");
        var inputs = new EnqueueRequest[]
        {
            new("", "work", "x"), new("scout", "", "x"), new("scout", "work", null!), new("scout", "work", "x", ParentId: "a_missing"),
            new("scout", "work", "x", ParentId: root.Id, RequesterKind: "bot", RequesterBot: "lead", RequestedLevel: "god"),
            new("scout\n", "work", "x"), new("../etc", "work", "x"), new(null!, "work", "x"),
        };
        foreach (var r in inputs)
        {
            var res = Rt.Enqueue(r);
            Assert.False(res.Ok && res.Reason == EnqueueReasons.Ok && r.To is null);
            Assert.False(string.IsNullOrEmpty(res.Reason));
        }
    }

    [Fact(DisplayName = "Tools: assign needs a roster turn; cancel only by the requester; status only for related work")]
    public async Task ToolRules()
    {
        var r = await _x.Tools.CallAsync(new Api.ToolCaller("scout", null, null), "assign", new JsonObject { ["to"] = "Builder", ["brief"] = "x" }, CancellationToken.None);
        Assert.Equal(409, r.Status);
        var root = Running("lead");
        var child = Child(root, "Scout", "look into it");
        var cancelByOther = await _x.Tools.CallAsync(new Api.ToolCaller("builder", null, null), "cancel", new JsonObject { ["id"] = child.Id }, CancellationToken.None);
        Assert.False(cancelByOther.Ok);
        var status = await _x.Tools.CallAsync(new Api.ToolCaller("helper", null, null), "assignment_status", new JsonObject { ["id"] = child.Id }, CancellationToken.None);
        Assert.Equal(404, status.Status);
        var mine = await _x.Tools.CallAsync(new Api.ToolCaller("lead", null, root.Id), "assignment_status", new JsonObject(), CancellationToken.None);
        Assert.Contains(child.Id!, mine.Text, StringComparison.Ordinal);
        var cancel = await _x.Tools.CallAsync(new Api.ToolCaller("lead", null, root.Id), "cancel", new JsonObject { ["id"] = child.Id }, CancellationToken.None);
        Assert.True(cancel.Ok, cancel.Text);
        Assert.Equal(AssignmentStatus.Cancelled, Rt.Store.Get(child.Id!)!.Status);
    }
}

public sealed class HandoffLiveTests
{
    [Fact(DisplayName = "SCN-04 live: three real bots chain work, consult and report", Skip = "Needs Docker, the openmono-agent image with the MCP shim mounted, and the inference server through the relay. The fake ACP SCN-04 covers the runtime.")]
    public void Scn04Live() { }

    [Fact(DisplayName = "SCN-09 live: a real container sleeps when idle and wakes for work", Skip = "Needs Docker and the openmono-agent image. The fake engine SCN-09 covers the launcher and runtime.")]
    public void Scn09Live() { }
}

public sealed class HandoffScenarioTests : IAsyncLifetime
{
    private readonly TempRoster _r = new();
    private Roster _x = null!;

    public async Task InitializeAsync() => _x = await Roster.StartAsync(_r);

    public async Task DisposeAsync()
    {
        await _x.DisposeAsync();
        _r.Dispose();
    }

    private static JsonObject A(string to, string brief) => new() { ["to"] = to, ["brief"] = brief };

    [Fact(DisplayName = "SCN-04 three-bot chain: work to Builder, Builder consults Scout, Scout answers, Builder finishes, Lead reports; audit replay complete and in order")]
    public async Task Scn04()
    {
        string? consult = null;
        _x.Behavior["lead"] = m => m.StartsWith("[report", StringComparison.Ordinal)
            ? Roster.Say("Done: Builder fixed the test.")
            : (_x.Call("lead", "assign", A("Builder", "Fix the failing ACP test.")).Ok ? Roster.Say("Handed to Builder.") : Roster.Say("could not hand it out"));
        _x.Behavior["builder"] = m =>
        {
            if (m.StartsWith("[report", StringComparison.Ordinal)) return Roster.Say(m.Contains("AcpServerSettings.cs", StringComparison.Ordinal) ? "Fixed using AcpServerSettings.cs line 52." : "no answer seen");
            var r = _x.Call("builder", "consult", A("Scout", "Which file holds the ACP pause enum?"));
            consult = r.Data?["id"]?.GetValue<string>();
            return Roster.Say("Asked Scout.");
        };
        _x.Behavior["scout"] = _ =>
        {
            var gate = TurnGate.Read(_r.Paths, "scout");
            return Roster.Say(gate.Origin == "consult" ? "It is in AcpServerSettings.cs line 52." : "wrong gate");
        };
        var root = await _x.PersonAsync("lead", "Please get the ACP test passing.");
        Assert.Equal(AssignmentStatus.Waiting, root.Status);
        await _x.DrainAsync();
        var tree = _x.Runtime.Store.Tree(root.Id);
        Assert.Equal(3, tree.Count);
        Assert.All(tree, a => Assert.Equal(AssignmentStatus.Completed, a.Status));
        Assert.Equal("Done: Builder fixed the test.", tree.Single(a => a.IsRoot).Result);
        var scoutRow = _x.Runtime.Store.Get(consult!)!;
        Assert.Equal(("consult", 2, "It is in AcpServerSettings.cs line 52."), (scoutRow.Mode, scoutRow.Depth, scoutRow.Result));
        Assert.Equal(["plan"], _x.Servers["scout"].Sessions.Values.SelectMany(s => s.ModeAtMessage).ToArray());
        Assert.Contains("Fixed using AcpServerSettings.cs", tree.Single(a => a.AssigneeBot == "builder").Result, StringComparison.Ordinal);
        // Audit replay: every assignment's events in order, through `oma-bot audit tree`.
        var o = new StringWriter();
        var env = new CliEnv { Paths = _r.Paths, AppDir = _r.AppDir, Clock = _r.Clock, Out = o, Err = new StringWriter() };
        Assert.Equal(0, await new CliApp(env).RunAsync(["audit", "tree", root.Id]));
        var text = o.ToString();
        foreach (var a in tree) Assert.Contains(a.Id, text, StringComparison.Ordinal);
        var events = _x.Runtime.Store.TreeEvents(root.Id).Select(e => (e.AssignmentId, e.To)).ToList();
        int First(string id, string to) => events.FindIndex(e => e.AssignmentId == id && e.To == to);
        var builderId = tree.Single(a => a.AssigneeBot == "builder").Id;
        Assert.True(First(builderId, "queued") < First(consult!, "queued"));
        Assert.True(First(consult!, "completed") < First(builderId, "completed"));
        Assert.True(First(builderId, "completed") < First(root.Id, "completed"));
        _x.AssertInvariants();
    }

    [Fact(DisplayName = "SCN-05 a ping-pong loop is stopped by the ancestor rule and a long chain by depth, inside the budget")]
    public async Task Scn05()
    {
        var refusals = new List<string>();
        _x.Behavior["lead"] = m => m.StartsWith("[report", StringComparison.Ordinal) ? Roster.Say("ok") : Hand("lead", "Scout", "Ping pong step one.");
        _x.Behavior["scout"] = m => m.StartsWith("[report", StringComparison.Ordinal) ? Roster.Say("ok") : Hand("scout", "Lead", "Ping pong back to you.", "Builder", "Chain step two.");
        _x.Behavior["builder"] = m => m.StartsWith("[report", StringComparison.Ordinal) ? Roster.Say("ok") : Hand("builder", "Editor", "Chain step three.");
        _x.Behavior["editor"] = m => m.StartsWith("[report", StringComparison.Ordinal) ? Roster.Say("ok") : Hand("editor", "Helper", "Chain step four.");
        IEnumerable<(string, object)> Hand(string me, params string[] pairs)
        {
            for (var i = 0; i < pairs.Length; i += 2)
            {
                var r = _x.Call(me, "assign", A(pairs[i], pairs[i + 1]));
                if (!r.Ok) refusals.Add(r.Data!["reason"]!.GetValue<string>());
            }
            return Roster.Say(me + " handed out what it could");
        }
        var root = await _x.PersonAsync("lead", "Start the chain.");
        await _x.DrainAsync();
        Assert.Contains(EnqueueReasons.Ancestor, refusals);
        Assert.Contains(EnqueueReasons.Depth, refusals);
        var tree = _x.Runtime.Store.Tree(root.Id);
        Assert.True(tree.Max(a => a.Depth) <= 3);
        Assert.True(tree.Sum(a => a.Executions) <= _r.Config.Handoffs.TreeTurnBudget);
        Assert.All(tree, a => Assert.True(a.IsTerminal));
        _x.AssertInvariants();
    }

    [Fact(DisplayName = "M5 a crash mid-chain loses no accepted assignment: a cut turn reruns once, one that acted outward is never repeated")]
    public async Task CrashMidChain()
    {
        _x.Behavior["lead"] = m => m.StartsWith("[report", StringComparison.Ordinal) ? Roster.Say("all done") : Do(() => { _x.Call("lead", "assign", A("Builder", "Build it.")); _x.Call("lead", "assign", A("Scout", "Read it.")); }, "handed out two");
        static IEnumerable<(string, object)> Do(Action act, string text) { act(); return Roster.Say(text); }
        var root = await _x.PersonAsync("lead", "Do the two things.");
        var children = _x.Runtime.Store.Children(root.Id);
        // Both child turns were admitted when the service died: one had already run a host command.
        foreach (var c in children)
            Assert.True(_x.Runtime.Store.Transition(c.Id, [AssignmentStatus.Queued], AssignmentStatus.Running, "roster", "admitted",
                new Dictionary<string, object?> { ["lease_owner"] = "turn_dead_" + c.AssigneeBot, ["started_at"] = AssignmentStore.Iso(_r.Clock.UtcNow) }));
        var builder = children.Single(c => c.AssigneeBot == "builder");
        _x.Runtime.Store.RecordOutward("builder", builder.Id, null, "turn_dead_builder", "HostExec", "HostExec: systemctl restart strata", "started");
        _r.Clock.Advance(TimeSpan.FromSeconds(5));
        _x.Restart();
        var lines = _x.Runtime.Reconcile();
        Assert.Equal(2, lines.Count);
        Assert.Equal((AssignmentStatus.Failed, Outcomes.Interrupted), (_x.Runtime.Store.Get(builder.Id)!.Status, _x.Runtime.Store.Get(builder.Id)!.Outcome));
        var scout = _x.Runtime.Store.Get(children.Single(c => c.AssigneeBot == "scout").Id)!;
        Assert.Equal((AssignmentStatus.Queued, 1), (scout.Status, scout.Restarts));
        await _x.DrainAsync();
        Assert.Equal(AssignmentStatus.Completed, _x.Runtime.Store.Get(scout.Id)!.Status);
        Assert.Contains("restart cut your last turn", _x.Servers["scout"].Sessions.Values.SelectMany(s => s.Messages).Single(), StringComparison.Ordinal);
        Assert.Equal(AssignmentStatus.Completed, _x.Runtime.Store.Get(root.Id)!.Status);
        Assert.Empty(_x.Servers["builder"].Sessions.Values.SelectMany(s => s.Messages));
        _x.AssertInvariants();
    }

    [Fact(DisplayName = "Restart: a turn cut after it handed out work waits for that work; a turn cut twice fails")]
    public async Task ReconcileCases()
    {
        _x.Behavior["lead"] = m => m.StartsWith("[report", StringComparison.Ordinal) ? Roster.Say("collected") : Roster.Say(_x.Call("lead", "assign", A("Scout", "Look it up.")).Text);
        var root = await _x.PersonAsync("lead", "Go.");
        // Pretend the root's turn was still running when the service died, after handing out work.
        _x.Db.Exec("UPDATE assignments SET status='running', lease_owner='turn_dead' WHERE id=$i;", ("$i", root.Id));
        _x.Restart();
        _x.Runtime.Reconcile();
        Assert.Equal(AssignmentStatus.Waiting, _x.Runtime.Store.Get(root.Id)!.Status);
        await _x.DrainAsync();
        Assert.Equal(AssignmentStatus.Completed, _x.Runtime.Store.Get(root.Id)!.Status);
        var twice = _x.Runtime.Enqueue(new EnqueueRequest("helper", "work", "cut twice", Rework: true));
        _x.Runtime.Store.Transition(twice.Id!, [AssignmentStatus.Queued], AssignmentStatus.Running, "t", "admitted", new Dictionary<string, object?> { ["restarts"] = 1, ["lease_owner"] = "x" });
        _x.Restart();
        _x.Runtime.Reconcile();
        Assert.Equal((AssignmentStatus.Failed, Outcomes.Error), (_x.Runtime.Store.Get(twice.Id!)!.Status, _x.Runtime.Store.Get(twice.Id!)!.Outcome));
    }

    [Fact(DisplayName = "One answer to the same request: a second requester waits for the first answer and gets it once")]
    public async Task SameRequest()
    {
        const string question = "Which file defines the PendingResponseKind enum in OMA core?";
        _x.Behavior["lead"] = m => m.StartsWith("[report", StringComparison.Ordinal) ? Roster.Say("lead got it") : Roster.Say(_x.Call("lead", "consult", A("Scout", question)).Text);
        _x.Behavior["builder"] = m => m.StartsWith("[report", StringComparison.Ordinal) ? Roster.Say("builder got it") : Roster.Say(_x.Call("builder", "consult", A("Scout", question.ToUpperInvariant())).Text);
        _x.Behavior["scout"] = _ => Roster.Say("AcpServerSettings.cs");
        var a = await _x.PersonAsync("lead", "first");
        var b = await _x.PersonAsync("builder", "second");
        var joined = _x.Runtime.Store.Children(b.Id).Single();
        Assert.Equal(AssignmentStatus.Waiting, joined.Status);
        Assert.NotNull(joined.JoinedTo);
        await _x.DrainAsync();
        Assert.Single(_x.Servers["scout"].Sessions.Values.SelectMany(s => s.Messages));
        Assert.Equal(("AcpServerSettings.cs", AssignmentStatus.Completed), (_x.Runtime.Store.Get(joined.Id)!.Result, _x.Runtime.Store.Get(joined.Id)!.Status));
        Assert.Contains("answered once for both", _x.Runtime.Store.Get(joined.Id)!.OutcomeDetail, StringComparison.Ordinal);
        Assert.Equal(AssignmentStatus.Completed, _x.Runtime.Store.Get(a.Id)!.Status);
        Assert.Equal(AssignmentStatus.Completed, _x.Runtime.Store.Get(b.Id)!.Status);
        _x.AssertInvariants();
    }

    [Fact(DisplayName = "Ask: a question to Spencer parks the work; his answer resumes it. A question to the requester wakes it; its answer resumes the child")]
    public async Task Questions()
    {
        _x.Behavior["scout"] = m => m.Contains("[answer from Spencer", StringComparison.Ordinal)
            ? Roster.Say("Using branch main as told.")
            : Roster.Say(_x.Call("scout", "ask", new JsonObject { ["question"] = "Which branch should I read?" }).Text);
        var root = await _x.PersonAsync("scout", "Read the branch for me.");
        Assert.Equal(AssignmentStatus.NeedsInput, root.Status);
        var card = Assert.Single(_x.Runtime.Approvals.Pending());
        Assert.Equal(ApprovalKinds.Ask, card.Kind);
        _x.AssertInvariants(quiesced: true);
        Assert.True(_x.Runtime.Approvals.Decide(card.Id, Approver.Person("person:test"), true, null, card.ActionHash, PersonOnlyPolicy.Instance, "main").Ok);
        await _x.DrainAsync();
        Assert.Equal(("Using branch main as told.", AssignmentStatus.Completed), (_x.Runtime.Store.Get(root.Id)!.Result, _x.Runtime.Store.Get(root.Id)!.Status));

        _x.Behavior["lead"] = m => m.StartsWith("[question from", StringComparison.Ordinal)
            ? Roster.Say(_x.Call("lead", "answer", new JsonObject { ["id"] = QuestionId(m), ["answer"] = "Use the sandbox checkout." }).Text)
            : m.StartsWith("[report", StringComparison.Ordinal) ? Roster.Say("lead finished") : Roster.Say(_x.Call("lead", "assign", A("Builder", "Fix it in the right checkout.")).Text);
        _x.Behavior["builder"] = m => m.Contains("[answer from your requester", StringComparison.Ordinal)
            ? Roster.Say("Fixed in the sandbox checkout.")
            : Roster.Say(_x.Call("builder", "ask", new JsonObject { ["question"] = "Which checkout?", ["to"] = "requester" }).Text);
        var root2 = await _x.PersonAsync("lead", "Fix it.");
        await _x.DrainAsync();
        var child = _x.Runtime.Store.Children(root2.Id).Single();
        Assert.Equal(("Fixed in the sandbox checkout.", AssignmentStatus.Completed), (child.Result, child.Status));
        Assert.Equal(AssignmentStatus.Completed, _x.Runtime.Store.Get(root2.Id)!.Status);
        _x.AssertInvariants();
        static string QuestionId(string m) => m["[question from ".Length..].Split(' ')[0];
    }

    [Fact(DisplayName = "Notify is one-way: no reply, the parent does not wait, changing tools are off for the notice turn")]
    public async Task NotifyOneWay()
    {
        IReadOnlyList<string>? gate = null;
        _x.Behavior["lead"] = _ => Roster.Say(_x.Call("lead", "notify", A("Builder", "FYI the nightly build failed.")).Text);
        _x.Behavior["builder"] = _ => { gate = TurnGate.Read(_r.Paths, "builder").Blocked; return Roster.Say("noted"); };
        var root = await _x.PersonAsync("lead", "Tell Builder.");
        Assert.Equal(AssignmentStatus.Completed, root.Status);
        await _x.DrainAsync();
        var notice = _x.Runtime.Store.Children(root.Id).Single();
        Assert.Equal(("notify", "none", AssignmentStatus.Completed), (notice.Mode, notice.Reply, notice.Status));
        Assert.Contains("FileWrite", gate!);
        Assert.Contains("Bash", gate!);
        _x.AssertInvariants();
    }

    [Fact(DisplayName = "SCN-10 a consulted bot runs in plan mode with changing tools gated, so a write is refused")]
    public async Task Scn10()
    {
        string? origin = null;
        IReadOnlyList<string>? blocked = null;
        _x.Behavior["scout"] = m => m.StartsWith("[report", StringComparison.Ordinal) ? Roster.Say("ok") : Roster.Say(_x.Call("scout", "consult", A("Builder", "Can you look at the parser?")).Text);
        _x.Behavior["builder"] = _ =>
        {
            (origin, blocked) = TurnGate.Read(_r.Paths, "builder");
            return Roster.Say("The parser is fine.");
        };
        await _x.PersonAsync("scout", "Ask Builder.");
        await _x.DrainAsync();
        Assert.Equal("consult", origin);
        foreach (var t in new[] { "FileWrite", "FileEdit", "Bash" }) Assert.Contains(t, blocked!);
        Assert.Equal(["plan"], _x.Servers["builder"].Sessions.Values.SelectMany(s => s.ModeAtMessage).ToArray());
        _x.AssertInvariants();
    }

    [Fact(DisplayName = "SCN-09 a sleeping bot is woken by an assignment, finishes, and sleeps again after its idle time")]
    public async Task Scn09()
    {
        var engine = new Containers.FakeContainerEngine();
        var launcher = new BotLauncher(_x.Db, _r.Config, _r.Paths, _r.Clock, _r.Audit(_x.Db), _x.Notices, new DirectSupervisor(engine, _r.Config.Bots.Network),
            port => new Acp.AcpClient(new HttpClient(), _x.Servers[_x.Bots.All().Single(b => b.Port == port).Slug].BaseUrl), new LaunchOptions(_r.AppDir, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10)));
        _x.EnsureRunning = async (name, ct) => { var r = await launcher.UpAsync(name, ct); return r.Ok ? null : r.Message; };
        _x.Bots.SetState("helper", BotStates.Sleeping);
        var e = _x.Runtime.Enqueue(new EnqueueRequest("helper", "work", "Wake up and check the queue.", Rework: true));
        await _x.DrainAsync();
        Assert.Equal(AssignmentStatus.Completed, _x.Runtime.Store.Get(e.Id!)!.Status);
        Assert.Equal(BotStates.Running, _x.Bots.Get("helper").State);
        Assert.Contains(engine.Calls, c => c.StartsWith("start", StringComparison.Ordinal));
        var sleeper = new IdleSleeper(_x.Db, _r.Clock, launcher);
        Assert.DoesNotContain(await sleeper.TickAsync(CancellationToken.None), l => l.Contains("helper", StringComparison.OrdinalIgnoreCase));
        _r.Clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Contains(await sleeper.TickAsync(CancellationToken.None), l => l.StartsWith("Helper is idle", StringComparison.Ordinal));
        Assert.Equal(BotStates.Sleeping, _x.Bots.Get("helper").State);
        _x.AssertInvariants();
    }

    [Fact(DisplayName = "Deadline: a tree past its deadline fails every live node; cancel cascades and aborts")]
    public async Task DeadlineAndCancel()
    {
        _x.Behavior["lead"] = m => m.StartsWith("[report", StringComparison.Ordinal) ? Roster.Say("x") : Roster.Say(_x.Call("lead", "assign", A("Scout", "Slow work.")).Text);
        var root = await _x.PersonAsync("lead", "Start slow work.");
        _r.Clock.Advance(TimeSpan.FromMinutes(_r.Config.Handoffs.TreeDeadlineMinutes + 1));
        await _x.Runtime.TickAsync(CancellationToken.None);
        Assert.All(_x.Runtime.Store.Tree(root.Id), a => Assert.Equal((AssignmentStatus.Failed, Outcomes.Expired), (a.Status, a.Outcome)));
        Assert.Equal(1, _x.Notices.Count("assignment:"));
        var root2 = await _x.PersonAsync("lead", "Start more slow work.");
        var res = await _x.Runtime.CancelAsync(root2.Id, "person:test", "changed my mind");
        Assert.Equal(2, res.Cancelled);
        Assert.All(_x.Runtime.Store.Tree(root2.Id), a => Assert.Equal(AssignmentStatus.Cancelled, a.Status));
        _x.AssertInvariants();
    }

    [Fact(DisplayName = "Routine runs are root assignments: origin routine, ask level, the routine gate, the run linked to its assignment")]
    public async Task RoutineRoot()
    {
        IReadOnlyList<string>? blocked = null;
        _x.Behavior["builder"] = _ => { blocked = TurnGate.Read(_r.Paths, "builder").Blocked; return Roster.Say("routine done"); };
        var routines = new RoutineStore(_x.Db, _r.Clock);
        var bot = _x.Bots.Get("Builder");
        var spec = new Team.RoutineSpec { Name = "nightly", IntervalMinutes = 60, Instructions = "Check the build.", Timezone = "UTC" };
        routines.Sync([bot with { Spec = Clone(bot.Spec, spec) }]);
        var st = routines.Find("builder", "nightly")!;
        var lease = _x.Runtime.Turns.TryAcquire("builder", Priority.Routine, "nightly", TimeSpan.FromMinutes(20))!;
        var runId = routines.TryStartRun(st.Id, "builder", "nightly", _r.Clock.UtcNow, "interval", RoutineStore.Status.Running, lease.Owner)!.Value;
        var result = await new AssignmentRoutineRunner(_x.Runtime, _r.Config, _r.Clock).RunAsync(new RoutineRunRequest(runId, bot, spec, "interval", _r.Clock.UtcNow, "build broke", lease), CancellationToken.None);
        _x.Runtime.Turns.Release(lease);
        Assert.Equal(RoutineStore.Status.Ok, result.Status);
        var run = routines.Run(runId)!;
        var a = _x.Runtime.Store.Get(run.AssignmentId!)!;
        Assert.Equal(("routine", "ask", AssignmentStatus.Completed, runId), (a.Origin, a.Level, a.Status, a.RoutineRunId));
        Assert.Contains("FileWrite", blocked!);
        _x.AssertInvariants();
        static Team.BotSpec Clone(Team.BotSpec s, Team.RoutineSpec r) { var c = s.Clone(); c.Routines = [r]; return c; }
    }

    [Fact(DisplayName = "oma-bot run makes a person's message a root assignment; assignments show and audit tree read it")]
    public async Task CliRunIsRoot()
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var env = new CliEnv
        {
            Paths = _r.Paths, AppDir = _r.AppDir, Clock = _r.Clock, Out = o, Err = e,
            Acp = port => new Acp.AcpClient(new HttpClient(), _x.Servers[_x.Bots.All().Single(b => b.Port == port).Slug].BaseUrl),
        };
        _x.Behavior["scout"] = _ => Roster.Say("Hello from Scout.");
        Assert.Equal(0, await new CliApp(env).RunAsync(["run", "Scout", "Who are you?"]));
        Assert.Contains("Hello from Scout.", o.ToString(), StringComparison.Ordinal);
        var root = _x.Runtime.Store.Recent(5, false).Single();
        Assert.Equal(("person", "scout", AssignmentStatus.Completed, "interactive"), (root.Origin, root.AssigneeBot, root.Status, root.Priority));
        o.GetStringBuilder().Clear();
        Assert.Equal(0, await new CliApp(env).RunAsync(["assignments", "show", root.Id]));
        Assert.Contains("Hello from Scout.", o.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, await new CliApp(env).RunAsync(["assignments", "--all"]));
        _x.AssertInvariants();
    }
}
