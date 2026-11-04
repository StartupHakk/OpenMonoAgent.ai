// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Omabot.Common;
using Omabot.Config;
using Omabot.Gateway;
using Omabot.Tests.Support;

namespace Omabot.Tests;

public sealed class LaneQueueTests
{
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 3, 17, 0, 0, TimeSpan.Zero));

    [Fact(DisplayName = "UNIT-GW-01 round robin by bot within a priority")]
    public async Task Gw01()
    {
        var q = new LaneQueue(1, 2, _clock);
        var hold = await q.EnterAsync("x", Priority.Assignment);
        var order = new List<string>();
        var tasks = new List<Task<GatewayLease>>();
        foreach (var bot in new[] { "a", "a", "a", "b", "b", "b", "c", "c", "c" }) tasks.Add(q.EnterAsync(bot, Priority.Assignment));
        hold.Dispose();
        for (var i = 0; i < 9; i++)
        {
            var done = await Task.WhenAny(tasks);
            tasks.Remove(done);
            var lease = await done;
            order.Add(lease.Bot);
            lease.Dispose();
        }
        Assert.Equal(["a", "b", "c", "a", "b", "c", "a", "b", "c"], order);
    }

    [Fact(DisplayName = "UNIT-GW-02 interactive before assignment before routine before background")]
    public async Task Gw02()
    {
        var q = new LaneQueue(1, 2, _clock);
        var hold = await q.EnterAsync("x", Priority.Interactive);
        var bg = q.EnterAsync("d", Priority.Background);
        var rt = q.EnterAsync("c", Priority.Routine);
        var asg = q.EnterAsync("b", Priority.Assignment);
        var it = q.EnterAsync("a", Priority.Interactive);
        hold.Dispose();
        var order = new List<Priority>();
        var pending = new List<Task<GatewayLease>> { bg, rt, asg, it };
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            var l = await done;
            order.Add(l.Priority);
            l.Dispose();
        }
        Assert.Equal([Priority.Interactive, Priority.Assignment, Priority.Routine, Priority.Background], order);
    }

    [Fact(DisplayName = "UNIT-GW-03 background is dropped when the queue would be longer than S")]
    public async Task Gw03()
    {
        var q = new LaneQueue(1, 2, _clock);
        var bgFree = await q.EnterAsync("m", Priority.Background); // a free lane serves background at once
        var waiting = q.EnterAsync("a", Priority.Assignment);
        await Assert.ThrowsAsync<QueueDroppedException>(() => q.EnterAsync("m", Priority.Background));
        Assert.Equal(1, q.Waiting);
        bgFree.Dispose();
        (await waiting).Dispose();
    }

    [Fact(DisplayName = "Per-bot cap of 2 in flight holds even with free lanes")]
    public async Task PerBotCap()
    {
        var q = new LaneQueue(4, 2, _clock);
        var a1 = await q.EnterAsync("a", Priority.Interactive);
        var a2 = await q.EnterAsync("a", Priority.Interactive);
        var a3 = q.EnterAsync("a", Priority.Interactive);
        var b1 = await q.EnterAsync("b", Priority.Routine);
        Assert.False(a3.IsCompleted);
        Assert.Equal(3, q.InFlight);
        a1.Dispose();
        var l3 = await a3;
        Assert.Equal(3, q.InFlight);
        foreach (var l in new[] { a2, l3, b1 }) l.Dispose();
        Assert.Equal(0, q.InFlight);
    }

    [Fact(DisplayName = "Sticky slots: a bot gets its last lane back when it is free")]
    public async Task Sticky()
    {
        var q = new LaneQueue(3, 2, _clock);
        var a = await q.EnterAsync("a", Priority.Assignment);
        var b = await q.EnterAsync("b", Priority.Assignment);
        Assert.NotEqual(a.Lane, b.Lane);
        var bLane = b.Lane;
        b.Dispose();
        a.Dispose();
        var c = await q.EnterAsync("c", Priority.Assignment);
        var b2 = await q.EnterAsync("b", Priority.Assignment);
        Assert.Equal(bLane, b2.Lane);
        c.Dispose(); b2.Dispose();
    }

    [Fact(DisplayName = "A cancelled waiter leaves the queue and never gets a lane")]
    public async Task Cancel()
    {
        var q = new LaneQueue(1, 2, _clock);
        var hold = await q.EnterAsync("x", Priority.Assignment);
        using var cts = new CancellationTokenSource();
        var w = q.EnterAsync("a", Priority.Assignment, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w);
        Assert.Equal(0, q.Waiting);
        hold.Dispose();
        Assert.Equal(0, q.InFlight);
    }

    [Fact(DisplayName = "Raising S after discovery serves waiters; lowering waits for lanes to free")]
    public async Task SetSlots()
    {
        var q = new LaneQueue(1, 2, _clock);
        var a = await q.EnterAsync("a", Priority.Assignment);
        var b = q.EnterAsync("b", Priority.Assignment);
        q.SetSlots(2);
        var bl = await b;
        q.SetSlots(1);
        Assert.Equal(1, q.Slots);
        var c = q.EnterAsync("c", Priority.Assignment);
        a.Dispose();
        Assert.False(c.IsCompleted);
        bl.Dispose();
        (await c).Dispose();
    }

    [Fact(DisplayName = "Disposing a lease twice frees the lane once")]
    public async Task DoubleDispose()
    {
        var q = new LaneQueue(2, 2, _clock);
        var a = await q.EnterAsync("a", Priority.Assignment);
        var b = await q.EnterAsync("b", Priority.Assignment);
        a.Dispose(); a.Dispose();
        Assert.Equal(1, q.InFlight);
        b.Dispose();
    }
}

public sealed class SlotTests
{
    [Fact(DisplayName = "INT-07 slot discovery reads concurrency.serving from Strata /v1/status (stub server)")]
    public async Task Int07Strata()
    {
        var h = new StubHandler((req, _) => Task.FromResult(req.RequestUri!.AbsolutePath == "/v1/status"
            ? StubHandler.Json("""{"model":"x","concurrency":{"serving":3,"max":8}}""")
            : StubHandler.Json("{}", System.Net.HttpStatusCode.NotFound)));
        var r = await SlotDiscovery.DiscoverAsync(new HttpClient(h), "https://relay.example.test/v1", "k-0123456789abcdef", "strata");
        Assert.Equal(3, r.Slots);
        Assert.Contains("concurrency.serving", r.Source);
        Assert.Equal("Bearer k-0123456789abcdef", h.Seen[0].Auth);
    }

    [Fact(DisplayName = "INT-07 slot discovery reads /slots on llama-server (stub server)")]
    public async Task Int07Llama()
    {
        var h = new StubHandler((req, _) => Task.FromResult(req.RequestUri!.AbsolutePath == "/slots"
            ? StubHandler.Json("""[{"id":0},{"id":1}]""")
            : StubHandler.Json("{}", System.Net.HttpStatusCode.NotFound)));
        var r = await SlotDiscovery.DiscoverAsync(new HttpClient(h), "http://127.0.0.1:8080/v1", null, "llama");
        Assert.Equal(2, r.Slots);
        Assert.Equal("http://127.0.0.1:8080/slots", h.Seen[0].Url);
    }

    [Fact(DisplayName = "Slot discovery reports a problem instead of guessing when the server says nothing")]
    public async Task NoGuess()
    {
        var h = new StubHandler((_, _) => Task.FromResult(StubHandler.Json("""{"status":"ok"}""")));
        var r = await SlotDiscovery.DiscoverAsync(new HttpClient(h), "https://relay.example.test/v1", null, "strata");
        Assert.Null(r.Slots);
        Assert.NotNull(r.Problem);
    }

    [Fact(DisplayName = "Slot order: measured node class, then gateway.slots, then server, then one lane marked unknown")]
    public void Resolver()
    {
        var server = new SlotReading(4, "strata", null);
        Assert.Equal((2, true), (SlotResolver.Decide(2, "3", server).Slots, SlotResolver.Decide(2, "3", server).Known));
        Assert.Equal(3, SlotResolver.Decide(null, "3", server).Slots);
        Assert.Equal(4, SlotResolver.Decide(null, "auto", server).Slots);
        var unknown = SlotResolver.Decide(null, "auto", new SlotReading(null, "none", "down"));
        Assert.Equal(1, unknown.Slots);
        Assert.False(unknown.Known);
    }
}

public sealed class RoutingAndUsageTests : IDisposable
{
    private readonly TempRoster _r = new();
    public void Dispose() => _r.Dispose();

    private const string Token = "omb_scouttokenscouttokenscout";
    private const string Token2 = "omb_buildertokenbuildertoken";

    private static RosterConfig TwoBackends()
    {
        var c = new RosterConfig { WorkspaceRoots = ["/tmp"] };
        c.Gateway.Backends.Add(new BackendSettings { Name = "main", Endpoint = "https://main.example.test/v1", KeyFile = "secrets/inference.key", Labels = ["class=main-3090", "model=flash-next"] });
        c.Gateway.Backends.Add(new BackendSettings { Name = "small", Endpoint = "https://small.example.test/v1", KeyFile = "secrets/small.key", Labels = ["class=small-node"], Models = new() { ["tiny"] = "Tiny-3B" } });
        return c;
    }

    [Fact(DisplayName = "UNIT-GW-05 a request needing class=main-3090 never goes to a small node backend")]
    public async Task Gw05()
    {
        var reg = new BackendRegistry(TwoBackends(), _r.Clock);
        var route = new ModelRoute("flash-next", "Qwen3.8-Flash-Next", "", "", "", ["class=main-3090"]);
        Assert.Equal(["main"], reg.Candidates(route).Select(b => b.Name));
        // Even with main fully busy, the small node is never picked.
        var hold = await reg.Get("main")!.Queue.EnterAsync("x", Priority.Interactive);
        Assert.Equal("main", reg.Pick(route)!.Name);
        hold.Dispose();
        var pinnedWrong = route with { Backend = "small" };
        Assert.Null(reg.Pick(pinnedWrong));
        var tiny = new ModelRoute("tiny", "Tiny-3B", "", "", "", ["class=small-node"]);
        Assert.Equal("small", reg.Pick(tiny)!.Name);
    }

    [Fact(DisplayName = "Implicit main backend from gateway.upstream carries the main labels (B22)")]
    public void ImplicitMain()
    {
        var reg = new BackendRegistry(_r.Config, _r.Clock);
        var main = Assert.Single(reg.All);
        Assert.Equal("main", main.Name);
        Assert.Contains("class=main-3090", main.Labels);
        Assert.False(main.Slots.Known);
        Assert.Equal(1, main.Slots.Slots);
    }

    private PassThroughGateway Gw(StubHandler h, RosterConfig config, out TurnContexts contexts, out UsageMeter usage)
    {
        var db = _r.Db();
        contexts = new TurnContexts(db, _r.Clock, Priority.Assignment);
        usage = new UsageMeter(db, _r.Clock);
        var routes = new Dictionary<string, ModelRoute>(StringComparer.OrdinalIgnoreCase)
        {
            ["flash-next"] = new("flash-next", "Qwen3.8-Flash-Next", "", "", "", ["class=main-3090"]),
            ["tiny"] = new("tiny", "Tiny-3B", "", "", "", ["class=small-node"]),
        };
        return new PassThroughGateway(
            () => new Dictionary<string, string> { [SecureFile.Sha256Hex(Token)] = "scout", [SecureFile.Sha256Hex(Token2)] = "builder" },
            () => routes, _ => "sk-upstreamkey0123456789abcdef", new HttpClient(h), _r.Clock, false, TimeSpan.FromSeconds(60))
        {
            Backends = new BackendRegistry(config, _r.Clock),
            Contexts = contexts,
            Usage = usage,
        };
    }

    private static DefaultHttpContext Ctx(string token, string body)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = "/v1/chat/completions";
        ctx.Request.Headers.Authorization = "Bearer " + token;
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string Text(DefaultHttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        return new StreamReader(ctx.Response.Body).ReadToEnd();
    }

    [Fact(DisplayName = "Streaming pass-through keeps SSE chunks byte for byte and meters usage per bot and routine")]
    public async Task StreamAndUsage()
    {
        var upstreamText = "";
        var h = new StubHandler(async (_, _) =>
        {
            var resp = StubHandler.ChatStream(4);
            upstreamText = await resp.Content.ReadAsStringAsync();
            return StubHandler.ChatStream(4);
        });
        var gw = Gw(h, TwoBackends(), out var contexts, out var usage);
        contexts.Set("scout", Priority.Routine, "morning-digest", TimeSpan.FromMinutes(5));
        var ctx = Ctx(Token, """{"model":"flash-next","stream":true,"messages":[]}""");
        await gw.HandleAsync(ctx);
        Assert.Equal(200, ctx.Response.StatusCode);
        Assert.Equal(upstreamText, Text(ctx));
        Assert.StartsWith("https://main.example.test/v1/chat/completions", h.Seen[0].Url);
        var row = Assert.Single(usage.Summary(TimeSpan.FromDays(1)));
        Assert.Equal(("scout", "morning-digest", 1L, 120L, 4L), (row.Bot, row.Routine, row.Calls, row.PromptTokens, row.CompletionTokens));
        Assert.Equal(124, usage.TokensToday("scout", TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix")));
    }

    [Fact(DisplayName = "Per backend model names: a nickname maps to that backend's own model")]
    public async Task PerBackendModel()
    {
        var h = new StubHandler((_, _) => Task.FromResult(StubHandler.Json("""{"choices":[{"message":{"content":"hi"}}],"usage":{"prompt_tokens":5,"completion_tokens":1}}""")));
        var gw = Gw(h, TwoBackends(), out _, out var usage);
        var ctx = Ctx(Token, """{"model":"tiny","messages":[]}""");
        await gw.HandleAsync(ctx);
        Assert.Equal("Tiny-3B", JsonNode.Parse(h.Seen[0].Body)!["model"]!.GetValue<string>());
        Assert.StartsWith("https://small.example.test", h.Seen[0].Url);
        Assert.False(usage.Summary(TimeSpan.FromDays(1))[0].Estimated > 0);
    }

    [Fact(DisplayName = "No backend with the labels: 503 no_backend and nothing goes upstream")]
    public async Task NoBackend()
    {
        var c = TwoBackends();
        c.Gateway.Backends.RemoveAt(0);
        var h = new StubHandler((_, _) => Task.FromResult(StubHandler.Json("{}")));
        var gw = Gw(h, c, out _, out _);
        var ctx = Ctx(Token, """{"model":"flash-next","messages":[]}""");
        await gw.HandleAsync(ctx);
        Assert.Equal(503, ctx.Response.StatusCode);
        Assert.Contains("no_backend", Text(ctx));
        Assert.Empty(h.Seen);
    }

    [Fact(DisplayName = "Background traffic over the queue limit gets 429 queue_full")]
    public async Task BackgroundDropped()
    {
        var gate = new TaskCompletionSource();
        var h = new StubHandler(async (_, _) => { await gate.Task; return StubHandler.ChatStream(1); });
        var config = TwoBackends();
        var gw = Gw(h, config, out var contexts, out _);
        contexts.Set("builder", Priority.Background, null, TimeSpan.FromMinutes(5));
        var first = gw.HandleAsync(Ctx(Token, """{"model":"flash-next","messages":[]}"""));   // holds the one lane
        var second = gw.HandleAsync(Ctx(Token, """{"model":"flash-next","messages":[]}"""));  // waits
        await Task.Delay(50);
        var bg = Ctx(Token2, """{"model":"flash-next","messages":[]}""");
        await gw.HandleAsync(bg);
        Assert.Equal(429, bg.Response.StatusCode);
        gate.SetResult();
        await Task.WhenAll(first, second);
    }

    [Fact(DisplayName = "Slot affinity sends id_slot to llama-server backends only")]
    public async Task Affinity()
    {
        var c = TwoBackends();
        c.Gateway.Backends[0].Kind = "llama";
        c.Gateway.Backends[0].SlotAffinity = true;
        var h = new StubHandler((_, _) => Task.FromResult(StubHandler.Json("""{"choices":[]}""")));
        var gw = Gw(h, c, out _, out _);
        await gw.HandleAsync(Ctx(Token, """{"model":"flash-next","messages":[]}"""));
        await gw.HandleAsync(Ctx(Token, """{"model":"tiny","messages":[]}"""));
        Assert.Equal(0, JsonNode.Parse(h.Seen[0].Body)!["id_slot"]!.GetValue<int>());
        Assert.Null(JsonNode.Parse(h.Seen[1].Body)!["id_slot"]);
    }

    [Fact(DisplayName = "A bot cannot raise its own priority with a header; the roster's turn context decides")]
    public async Task NoSelfPriority()
    {
        var db = _r.Db();
        var contexts = new TurnContexts(db, _r.Clock, Priority.Assignment);
        Assert.Equal(Priority.Assignment, contexts.For("scout").Priority);
        contexts.Set("scout", Priority.Interactive, null, TimeSpan.FromMinutes(1));
        Assert.Equal(Priority.Interactive, contexts.For("scout").Priority);
        _r.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(Priority.Assignment, contexts.For("scout").Priority); // expired
        var src = File.ReadAllText(Path.Combine(Applied.RepoRoot(), "bot/src/Omabot.Core/Gateway/PassThroughGateway.cs"));
        Assert.DoesNotContain("X-Roster-Priority", src);
        Assert.DoesNotContain("Request.Headers[\"", src);
    }

    [Fact(DisplayName = "LOAD-01 (simulated upstream) 2S bots at S slots: never more than S in flight, no bot waits over 3x the median")]
    public async Task Load01Simulated()
    {
        const int slots = 2;
        var c = TwoBackends();
        c.Gateway.Slots = null;
        c.Gateway.NodeClasses.Add(new NodeClass { Name = "main-3090", MeasuredSlots = slots });
        c.Gateway.Backends[0].NodeClass = "main-3090";
        var h = new StubHandler(async (_, _) => { await Task.Delay(20); return StubHandler.ChatStream(2); });
        var db = _r.Db();
        var usage = new UsageMeter(db, SystemClock.Instance);
        var tokens = Enumerable.Range(0, 2 * slots).ToDictionary(i => SecureFile.Sha256Hex($"omb_loadbot{i}_tokentokentoken"), i => $"bot{i}");
        var routes = new Dictionary<string, ModelRoute> { ["flash-next"] = new("flash-next", "Q", "", "", "", ["class=main-3090"]) };
        var gw = new PassThroughGateway(() => tokens, () => routes, _ => "sk-upstreamkey0123456789abcdef", new HttpClient(h), SystemClock.Instance, false, TimeSpan.FromSeconds(60))
        {
            Backends = new BackendRegistry(c, SystemClock.Instance),
            Usage = usage,
        };
        var all = new List<Task>();
        for (var i = 0; i < 2 * slots; i++)
        {
            var token = $"omb_loadbot{i}_tokentokentoken";
            all.Add(Task.Run(async () =>
            {
                for (var k = 0; k < 6; k++) await gw.HandleAsync(Ctx(token, """{"model":"flash-next","stream":true,"messages":[]}"""));
            }));
            all.Add(Task.Run(async () =>
            {
                for (var k = 0; k < 6; k++) await gw.HandleAsync(Ctx(token, """{"model":"flash-next","stream":true,"messages":[]}"""));
            }));
        }
        await Task.WhenAll(all);
        Assert.True(h.Peak <= slots, $"server saw {h.Peak} in flight");
        Assert.Equal(slots, gw.Backends.Get("main")!.Queue.PeakInFlight);
        var rows = usage.Summary(TimeSpan.FromDays(1));
        Assert.Equal(2 * slots, rows.Count);
        Assert.All(rows, r => Assert.Equal(12, r.Calls));
        var waits = db.Query("SELECT bot, AVG(queue_ms) FROM usage GROUP BY bot;", r => r.GetDouble(1)).OrderBy(x => x).ToList();
        var median = (waits[1] + waits[2]) / 2;
        Assert.True(waits.Max() <= Math.Max(3 * median, 50), $"waits {string.Join(",", waits)}");
        var p95 = db.Query("SELECT queue_ms FROM usage ORDER BY queue_ms;", r => r.GetInt64(0)).ToList();
        Assert.True(p95[(int)(p95.Count * 0.95) - 1] < 10_000);
    }

    [Fact(Skip = "LOAD-01 live needs the real inference server through the relay and 2 running bots (Docker); run on the roster host")]
    public void Load01Live() { }

    [Fact(Skip = "INT-07 live needs a real Strata or llama-server; the stub server tests cover the parsing")]
    public void Int07Live() { }

    [Fact(Skip = "INT-13 needs the FRPS and FRPC relay and the main box; run on the roster host")]
    public void Int13() { }
}

public sealed class UsageSnifferTests
{
    [Fact(DisplayName = "Sniffer: streamed usage block wins; without one, content chunks are an estimate")]
    public void Sniffer()
    {
        var s = new UsageSniffer();
        var bytes = Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"a\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"b\"}}]}\n\ndata: [DONE]\n\n");
        // Feed in awkward pieces to prove chunk boundaries do not matter.
        for (var i = 0; i < bytes.Length; i += 7) s.Feed(bytes.AsSpan(i, Math.Min(7, bytes.Length - i)));
        s.Finish();
        Assert.True(s.Estimated);
        Assert.Equal(2, s.CompletionTokens);

        var j = new UsageSniffer();
        j.Feed(Encoding.UTF8.GetBytes("""{"choices":[{"message":{"content":"hi"}}],"usage":{"prompt_tokens":9,"completion_tokens":3}}"""));
        j.Finish();
        Assert.False(j.Estimated);
        Assert.Equal((9, 3), (j.PromptTokens, j.CompletionTokens));
    }
}
