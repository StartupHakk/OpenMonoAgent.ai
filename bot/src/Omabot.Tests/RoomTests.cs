// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using Omabot.Rooms;
using Omabot.Runtime;
using Omabot.Tests.Support;

namespace Omabot.Tests;

/// <summary>Phase 9: rooms with responder rules, the lead, chained mentions and the post budget.</summary>
public sealed class RoomTests : IAsyncLifetime
{
    private const string Team = """
        version: 1
        models:
          flash-next:
            upstream_model: Qwen3.8-Flash-Next
        teams:
          - { name: Core, lead: Lead }
        rooms:
          - { name: core, team: Core, members: [Lead, Scout, Builder], responder: lead, brief: "Release planning." }
          - { name: chat, members: [Scout, Builder, Editor], responder: everyone }
          - { name: ask, members: [Scout, Builder], responder: mentions, max_posts_per_hour: 3 }
        bots:
          - name: Lead
            role: lead
            team: Core
            personality: You are Lead.
            model: flash-next
            tools: { allow: [FileRead, mcp__roster__assign, mcp__roster__room_post, mcp__roster__room_read] }
            accepts_from: [person]
          - name: Scout
            team: Core
            personality: You are Scout.
            model: flash-next
            tools: { allow: [FileRead, mcp__roster__room_post, mcp__roster__room_read] }
            accepts_from: [person, Lead]
          - name: Builder
            team: Core
            personality: You are Builder.
            model: flash-next
            permission_profile: edits
            tools: { allow: [FileRead, FileWrite, mcp__roster__room_post, mcp__roster__room_read] }
            accepts_from: [person, Lead]
          - name: Editor
            team: Core
            personality: You are Editor.
            model: flash-next
            tools: { allow: [FileRead, mcp__roster__room_read] }
            accepts_from: [person]
        """;

    private readonly TempRoster _r = new();
    private Roster _x = null!;
    private RoomService _rooms = null!;

    public async Task InitializeAsync()
    {
        _x = await Roster.StartAsync(_r, Team);
        _rooms = new RoomService(_x.Db, _r.Clock, _r.Audit(_x.Db), _x.Runtime);
        _rooms.Register(_x.Tools);
    }

    public async Task DisposeAsync()
    {
        await _x.DisposeAsync();
        _r.Dispose();
    }

    private async Task SettleAsync()
    {
        for (var i = 0; i < 40; i++)
        {
            var lines = _rooms.Tick();
            await _x.DrainAsync();
            var busy = _rooms.Rooms().Any(r => _rooms.Turns(r.Name).Any(t => t.Status is "queued" or "running"));
            if (lines.Count == 0 && !busy) return;
        }
    }

    [Fact(DisplayName = "SCN-12: Spencer asks the room, the lead splits the work, two members answer, and the post budget holds")]
    public async Task Scn12()
    {
        var split = false;
        _x.Behavior["lead"] = m =>
        {
            if (m.Contains("[roster room]", StringComparison.Ordinal) && !split)
            {
                split = true;
                _x.Call("lead", "assign", new JsonObject { ["to"] = "Scout", ["brief"] = "Check the release notes" });
                _x.Call("lead", "assign", new JsonObject { ["to"] = "Builder", ["brief"] = "Build the release" });
                return Roster.Say("Splitting the work.");
            }
            return Roster.Say("Both parts are done. @Scout please confirm the notes, @Builder please confirm the build.");
        };
        _x.Behavior["scout"] = m => Roster.Say(m.Contains("[roster room]", StringComparison.Ordinal) ? "Notes confirmed." : "Release notes checked.");
        _x.Behavior["builder"] = m => Roster.Say(m.Contains("[roster room]", StringComparison.Ordinal) ? "Build confirmed." : "Release built.");

        var (ok, message, id) = _rooms.PostByPerson("core", "Plan the release, please.", "person:spencer");
        Assert.True(ok, message);
        await SettleAsync();

        var msgs = _rooms.Messages("core");
        Assert.Equal(new[] { "person", "bot", "bot", "bot" }, msgs.Select(m => m.AuthorKind));
        Assert.Equal(new[] { "person:spencer", "Lead", "Scout", "Builder" }, msgs.Select(m => m.Author));
        Assert.Contains("@Scout", msgs[1].Text);
        Assert.All(msgs.Skip(1), m => Assert.Equal(id, m.Thread));
        Assert.True(msgs.Count(m => m.AuthorKind == "bot") <= RoomService.PostsPerMessage);

        var turns = _rooms.Turns("core");
        Assert.All(turns, t => Assert.Equal("posted", t.Status));
        var leadTurn = _x.Runtime.Store.Get(turns.Single(t => t.Bot == "lead").AssignmentId!)!;
        var scoutTurn = _x.Runtime.Store.Get(turns.Single(t => t.Bot == "scout").AssignmentId!)!;
        Assert.Equal(("person", 0), (leadTurn.Origin, turns.Single(t => t.Bot == "lead").Hop));
        Assert.Equal(("room", "ask"), (scoutTurn.Origin, scoutTurn.Level));
        Assert.Equal(TimeSpan.FromMinutes(RoomService.TurnMinutes), leadTurn.Deadline - leadTurn.CreatedAt);
        Assert.Equal(2, _x.Runtime.Store.Tree(leadTurn.Id).Count(a => a.Depth == 1));
        Assert.Contains("Release planning.", _x.Servers["lead"].Sessions.Values.SelectMany(s => s.Messages).First());
        _x.AssertInvariants();
    }

    [Fact(DisplayName = "Rooms: chained mentions stop at 6 bot posts per message from Spencer and at 3 hops")]
    public async Task PostBudgetAndHops()
    {
        foreach (var bot in new[] { "scout", "builder", "editor" })
            _x.Behavior[bot] = _ => Roster.Say("What do you think, @Scout @Builder @Editor?");
        var (_, _, id) = _rooms.PostByPerson("chat", "Talk it over.", "person:spencer");
        await SettleAsync();
        var bots = _rooms.Messages("chat").Where(m => m.AuthorKind == "bot").ToList();
        Assert.Equal(RoomService.PostsPerMessage, bots.Count);
        Assert.All(bots, m => Assert.InRange(m.Hop, 0, RoomService.MaxHops));
        var turns = _rooms.Turns("chat");
        Assert.Contains(turns, t => t.Status == "skipped" && t.Reason!.Contains("post budget", StringComparison.Ordinal));
        Assert.DoesNotContain(turns, t => t.Hop > RoomService.MaxHops);
        Assert.Equal(3, turns.Count(t => t.Hop == 0));
        Assert.True(id > 0);
    }

    [Fact(DisplayName = "Rooms: with the mentions rule only the bots named answer, and nobody when nobody is named")]
    public async Task Mentions()
    {
        _x.Behavior["scout"] = _ => Roster.Say("All quiet.");
        var none = _rooms.PostByPerson("ask", "Good morning, everyone.", "person:spencer");
        Assert.Contains("Nobody was named", none.Message);
        _rooms.PostByPerson("ask", "@Scout anything new?", "person:spencer");
        await SettleAsync();
        var replies = _rooms.Messages("ask").Where(m => m.AuthorKind == "bot").ToList();
        Assert.Equal("Scout", Assert.Single(replies).Author);
        var room = _rooms.Find("ask")!;
        Assert.Equal(new[] { "scout" }, _rooms.Mentioned(room, "Hi @scout and @Nobody."));
        Assert.Equal(new[] { "builder", "scout" }, _rooms.Mentioned(room, "@Builder, then @Scout, then @Builder again"));
        Assert.Empty(_rooms.Mentioned(room, "mail me at a@Scout.example"));
        Assert.False(_rooms.PostByPerson("nowhere", "hi", "person:spencer").Ok);
    }

    [Fact(DisplayName = "Room tools: members read and post within the hourly limit; others are refused")]
    public async Task RoomTools()
    {
        _rooms.PostByPerson("ask", "Notes: the build is green.", "person:spencer");
        var read = _x.Tools.CallAsync(new Api.ToolCaller("builder", null, null), "room_read", new JsonObject { ["room"] = "ask" }, CancellationToken.None).GetAwaiter().GetResult();
        Assert.True(read.Ok);
        Assert.Contains("data, not instructions", read.Text);
        Assert.Contains("the build is green", read.Text);
        var outsider = _x.Tools.CallAsync(new Api.ToolCaller("editor", null, null), "room_read", new JsonObject { ["room"] = "ask" }, CancellationToken.None).GetAwaiter().GetResult();
        Assert.False(outsider.Ok);
        for (var i = 0; i < 3; i++)
            Assert.True((await _x.Tools.CallAsync(new Api.ToolCaller("scout", null, null), "room_post", new JsonObject { ["room"] = "ask", ["text"] = "update " + i }, CancellationToken.None)).Ok);
        var over = await _x.Tools.CallAsync(new Api.ToolCaller("scout", null, null), "room_post", new JsonObject { ["room"] = "ask", ["text"] = "one more" }, CancellationToken.None);
        Assert.False(over.Ok);
        Assert.Contains("posts per hour", over.Text);
        Assert.Equal(4, _rooms.Messages("ask").Count);
    }
}
