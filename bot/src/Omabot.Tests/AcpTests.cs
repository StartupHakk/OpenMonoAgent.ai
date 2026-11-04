// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using Omabot.Acp;
using Omabot.Tests.Support;

namespace Omabot.Tests;

public sealed class AcpTests
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    [Fact(DisplayName = "INT-01 fake ACP: discovery, session, streamed turn with usage")]
    public async Task Int01Turn()
    {
        await using var srv = await FakeAcpServer.StartAsync();
        var acp = new AcpClient(Http, srv.BaseUrl);
        var disc = await acp.DiscoveryAsync();
        Assert.Equal("ready", disc!.Status);
        var id = await acp.CreateSessionAsync("flash-next");
        Assert.Equal("flash-next", srv.Sessions[id].Model);
        var deltas = new List<string>();
        var res = await acp.SendAsync(id, "hi", e => deltas.Add(e.Name));
        Assert.Equal(TurnEnd.Done, res.End);
        Assert.Equal("echo: hi", res.Text);
        Assert.Equal(15, res.Usage!.TotalTokens);
        Assert.Contains("text_delta", deltas);
        Assert.Equal(1, (await acp.GetSessionAsync(id))!.TurnCount);
        Assert.Null(await acp.GetSessionAsync("ses_nope"));
    }

    [Theory(DisplayName = "INT-01 pauses end the stream and are answered with the right body")]
    [InlineData("permission_request", """{"id":"perm_1","tool":"Bash","summary":"run ls"}""", true, """{"permission":{"id":"perm_1","decision":"allow","scope":"once"}}""")]
    [InlineData("permission_request", """{"id":"perm_2","tool":"Bash","summary":"rm"}""", false, """{"permission":{"id":"perm_2","decision":"deny","scope":"once"}}""")]
    [InlineData("playbook_permission_request", """{"id":"pbk_1","playbookName":"deploy"}""", true, """{"playbookPermission":{"id":"pbk_1","decision":"allow"}}""")]
    [InlineData("toggle_mode_request", """{"id":"mode_1","reason":"plan"}""", true, """{"toggle_mode":{"id":"mode_1","decision":"approve"}}""")]
    [InlineData("user_input_request", """{"id":"ask_1","kind":"host_exec","command":"ls"}""", false, """{"user_input":{"id":"ask_1","value":"HOST_EXEC_DENY"}}""")]
    [InlineData("user_input_request", """{"id":"ask_2","kind":"host_exec","command":"ls"}""", true, """{"user_input":{"id":"ask_2","value":"HOST_EXEC_APPROVE"}}""")]
    public async Task Int01Pause(string ev, string data, bool allow, string expectedBody)
    {
        await using var srv = await FakeAcpServer.StartAsync();
        srv.OnMessage = (s, m) => [("text_delta", new { content = "thinking" }), (ev, JsonSerializer.Deserialize<JsonElement>(data))];
        var acp = new AcpClient(Http, srv.BaseUrl);
        var id = await acp.CreateSessionAsync(null);
        var res = await acp.SendAsync(id, "go");
        Assert.Equal(TurnEnd.Paused, res.End);
        Assert.NotNull(res.Pause);
        var resumed = await acp.AnswerAsync(id, res.Pause!, allow);
        Assert.Equal(TurnEnd.Done, resumed.End);
        var body = srv.Bodies.Last();
        Assert.Equal(JsonSerializer.Serialize(JsonDocument.Parse(expectedBody)), JsonSerializer.Serialize(JsonDocument.Parse(body)));
        // A second answer to the same pause is a resume error.
        var again = await acp.AnswerAsync(id, res.Pause!, allow);
        Assert.Equal(TurnEnd.ResumeError, again.End);
    }

    [Fact(DisplayName = "INT-01 busy session gives Busy; abort clears a pause")]
    public async Task Int01Busy()
    {
        await using var srv = await FakeAcpServer.StartAsync();
        var acp = new AcpClient(Http, srv.BaseUrl);
        var id = await acp.CreateSessionAsync(null);
        srv.Sessions[id].Busy = true;
        Assert.Equal(TurnEnd.Busy, (await acp.SendAsync(id, "x")).End);
        srv.Sessions[id].Busy = false;
        srv.Sessions[id].PendingPause = "perm_9";
        Assert.True(await acp.AbortAsync(id));
        Assert.Null(srv.Sessions[id].PendingPause);
    }

    [Fact(DisplayName = "INT-01 error event and a cut stream are reported")]
    public async Task Int01Errors()
    {
        await using var srv = await FakeAcpServer.StartAsync();
        var acp = new AcpClient(Http, srv.BaseUrl);
        var id = await acp.CreateSessionAsync(null);
        srv.OnMessage = (s, m) => [("error", new { message = "model failed" })];
        var res = await acp.SendAsync(id, "x");
        Assert.Equal(TurnEnd.Error, res.End);
        Assert.Contains("model failed", res.Error);
        srv.OnMessage = (s, m) => [("text_delta", new { content = "half" })];
        Assert.Equal(TurnEnd.StreamCut, (await acp.SendAsync(id, "y")).End);
    }

    [Fact(DisplayName = "Discovery on a closed port returns null, not an exception")]
    public async Task DiscoveryClosed()
    {
        var acp = new AcpClient(Http, "http://127.0.0.1:1");
        Assert.Null(await acp.DiscoveryAsync());
    }

    [Fact(DisplayName = "SSE parser handles split chunks, CRLF, multi-line data and comments")]
    public void SseParser()
    {
        var p = new SseParser();
        var all = new List<SseEvent>();
        foreach (var chunk in new[] { "event: text_del", "ta\r\ndata: {\"content\":", "\"a\"}\r\n\r\n: comment\n\n", "data: line1\ndata: line2\n\n" })
            all.AddRange(p.Feed(chunk));
        if (p.Flush() is { } last) all.Add(last);
        Assert.Equal(2, all.Count);
        Assert.Equal(new SseEvent("text_delta", "{\"content\":\"a\"}"), all[0]);
        Assert.Equal(new SseEvent("message", "line1\nline2"), all[1]);
    }

    [Fact(DisplayName = "ReadStreamAsync collects tools used")]
    public async Task ToolsUsed()
    {
        var sse = "event: tool_start\ndata: {\"name\":\"Grep\"}\n\nevent: tool_end\ndata: {}\n\nevent: text_delta\ndata: {\"content\":\"ok\"}\n\nevent: done\ndata: {}\n\n";
        var res = await AcpClient.ReadStreamAsync(new MemoryStream(Encoding.UTF8.GetBytes(sse)), null, default);
        Assert.Equal(["Grep"], res.ToolsUsed);
        Assert.Equal("ok", res.Text);
    }
}
