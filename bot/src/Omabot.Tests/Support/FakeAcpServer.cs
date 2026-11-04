// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Omabot.Tests.Support;

/// <summary>
/// Minimal stand-in for an OMA ACP server (discovery, sessions, SSE turns,
/// pauses and resume rules), following the shapes in AcpEndpoints.cs and
/// AcpTurnRunner.cs. Used by INT-01 and the security tests.
/// </summary>
public sealed class FakeAcpServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public ConcurrentDictionary<string, FakeSession> Sessions { get; } = new();
    public ConcurrentQueue<string> Bodies { get; } = new();
    public bool Ready { get; set; } = true;

    /// <summary>Called for each user message; returns the script of SSE frames to send.</summary>
    public Func<FakeSession, string, IEnumerable<(string Event, object Data)>> OnMessage { get; set; } =
        (s, m) => [("text_delta", new { content = "echo: " + m }), ("usage", new { input_tokens = 10, output_tokens = 5, total_tokens = 15 }), ("done", new { })];

    public sealed class FakeSession
    {
        public string Id { get; init; } = "";
        public string Model { get; init; } = "";
        public int TurnCount { get; set; }
        public List<string> Messages { get; } = [];
        public string? PendingPause { get; set; }
        public string? PendingKind { get; set; }
        public List<string> Answers { get; } = [];
        public bool Busy { get; set; }
    }

    private FakeAcpServer(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public static async Task<FakeAcpServer> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        FakeAcpServer? self = null;
        app.MapGet("/api/v1/discovery", () => self!.Ready
            ? Results.Json(new { version = "fake", agent_id = "fake-agent", host_workspace = "/workspace", container_workspace = "/workspace", status = "ready", uptime_seconds = 5 })
            : Results.StatusCode(503));
        app.MapPost("/api/v1/sessions", async (HttpContext ctx) =>
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
            var model = doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "default";
            var s = new FakeSession { Id = "ses_" + Guid.NewGuid().ToString("N")[..8], Model = model };
            self!.Sessions[s.Id] = s;
            return Results.Json(new { session_id = s.Id, model });
        });
        app.MapGet("/api/v1/sessions/{id}", (string id) => self!.Sessions.TryGetValue(id, out var s)
            ? Results.Json(new { session_id = s.Id, model = s.Model, started_at = "2026-11-03T00:00:00Z", turn_count = s.TurnCount, plan_mode = false, host_enabled = false })
            : Results.NotFound());
        app.MapPost("/api/v1/sessions/{id}/turn", async (string id, HttpContext ctx) =>
        {
            if (!self!.Sessions.TryGetValue(id, out var s)) { ctx.Response.StatusCode = 404; return; }
            var raw = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            self.Bodies.Enqueue(raw);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.TryGetProperty("abort", out var ab) && ab.GetBoolean())
            {
                s.PendingPause = null;
                ctx.Response.StatusCode = 204;
                return;
            }
            if (s.Busy) { ctx.Response.StatusCode = 409; await ctx.Response.WriteAsJsonAsync(new { error = "session_busy" }); return; }
            IEnumerable<(string, object)> frames;
            if (root.TryGetProperty("message", out var msg))
            {
                s.TurnCount++;
                s.Messages.Add(msg.GetString() ?? "");
                frames = self.OnMessage(s, msg.GetString() ?? "");
            }
            else
            {
                var key = root.EnumerateObject().First();
                var pid = key.Value.GetProperty("id").GetString();
                if (s.PendingPause is null || pid != s.PendingPause)
                {
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsJsonAsync(new { error = "resume_error", detail = "unknown or already-resolved pause id" });
                    return;
                }
                s.Answers.Add(key.Name + ":" + key.Value.GetRawText());
                s.PendingPause = null;
                frames = [("text_delta", new { content = "resumed" }), ("done", new { })];
            }
            ctx.Response.ContentType = "text/event-stream";
            foreach (var (ev, data) in frames)
            {
                var json = JsonSerializer.Serialize(data);
                if (ev.EndsWith("_request", StringComparison.Ordinal))
                {
                    using var d = JsonDocument.Parse(json);
                    s.PendingPause = d.RootElement.GetProperty("id").GetString();
                    s.PendingKind = ev;
                }
                await ctx.Response.WriteAsync($"event: {ev}\ndata: {json}\n\n", Encoding.UTF8);
                await ctx.Response.Body.FlushAsync();
            }
        });
        await app.StartAsync();
        var addr = app.Urls.First();
        var port = new Uri(addr).Port;
        self = new FakeAcpServer(app, port);
        return self;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
