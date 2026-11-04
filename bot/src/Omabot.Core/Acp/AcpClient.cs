// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Omabot.Acp;

/// <summary>
/// Client for one bot's ACP server on 127.0.0.1. ACP has no auth, which is
/// why the port binds loopback only and bots are blocked from calling it.
/// </summary>
public sealed class AcpClient(HttpClient http, string baseUrl)
{
    private string Url(string path) => baseUrl.TrimEnd('/') + "/api/v1" + path;

    public async Task<AcpDiscovery?> DiscoveryAsync(CancellationToken ct = default)
    {
        try
        {
            using var r = await http.GetAsync(Url("/discovery"), ct).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var j = doc.RootElement;
            return new AcpDiscovery(
                j.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                j.TryGetProperty("agent_id", out var a) ? a.GetString() ?? "" : "",
                j.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "",
                j.TryGetProperty("uptime_seconds", out var u) && u.TryGetInt64(out var up) ? up : 0);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<string> CreateSessionAsync(string? model, CancellationToken ct = default)
    {
        using var r = await http.PostAsJsonAsync(Url("/sessions"), new { model }, ct).ConfigureAwait(false);
        var text = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"create session failed: {(int)r.StatusCode} {Common.Redactor.Shared.Redact(text)}");
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("session_id").GetString()!;
    }

    public async Task<AcpSessionInfo?> GetSessionAsync(string id, CancellationToken ct = default)
    {
        using var r = await http.GetAsync(Url("/sessions/" + Uri.EscapeDataString(id)), ct).ConfigureAwait(false);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var j = doc.RootElement;
        return new AcpSessionInfo(
            j.GetProperty("session_id").GetString()!,
            j.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "",
            j.TryGetProperty("turn_count", out var t) ? t.GetInt32() : 0,
            j.TryGetProperty("plan_mode", out var p) && p.ValueKind == JsonValueKind.True,
            j.TryGetProperty("host_enabled", out var h) && h.ValueKind == JsonValueKind.True);
    }

    public Task<AcpTurnResult> SendAsync(string session, string message, Action<SseEvent>? onEvent = null, CancellationToken ct = default)
        => TurnAsync(session, new { message }, onEvent, ct);

    public Task<AcpTurnResult> AnswerAsync(string session, AcpPause pause, bool allow, string? value = null, Action<SseEvent>? onEvent = null, CancellationToken ct = default)
        => TurnAsync(session, pause.Answer(allow, value), onEvent, ct);

    public async Task<bool> AbortAsync(string session, CancellationToken ct = default)
    {
        using var r = await http.PostAsJsonAsync(Url($"/sessions/{Uri.EscapeDataString(session)}/turn"), new { abort = true }, ct).ConfigureAwait(false);
        return r.StatusCode == HttpStatusCode.NoContent || r.IsSuccessStatusCode;
    }

    public async Task<AcpTurnResult> TurnAsync(string session, object body, Action<SseEvent>? onEvent, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, Url($"/sessions/{Uri.EscapeDataString(session)}/turn"))
        {
            Content = JsonContent.Create(body),
        };
        using var r = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (r.StatusCode == HttpStatusCode.Conflict) return new AcpTurnResult(TurnEnd.Busy, "", null, "session_busy", null, []);
        if (!r.IsSuccessStatusCode)
        {
            var err = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var end = err.Contains("resume_error", StringComparison.Ordinal) ? TurnEnd.ResumeError : TurnEnd.Error;
            return new AcpTurnResult(end, "", null, Common.Redactor.Shared.Redact(err), null, []);
        }
        await using var stream = await r.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await ReadStreamAsync(stream, onEvent, ct).ConfigureAwait(false);
    }

    /// <summary>Reads an SSE turn stream to its end. Public for tests.</summary>
    public static async Task<AcpTurnResult> ReadStreamAsync(Stream stream, Action<SseEvent>? onEvent, CancellationToken ct)
    {
        var parser = new SseParser();
        var text = new StringBuilder();
        var tools = new List<string>();
        AcpPause? pause = null;
        AcpUsage? usage = null;
        string? error = null;
        var done = false;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var buf = new char[4096];
        void Handle(SseEvent e)
        {
            onEvent?.Invoke(e);
            switch (e.Name)
            {
                case "text_delta":
                    text.Append(Field(e.Data, "content"));
                    break;
                case "tool_start":
                    var name = Field(e.Data, "name");
                    if (name.Length > 0) tools.Add(name);
                    break;
                case "usage":
                    try
                    {
                        using var doc = JsonDocument.Parse(e.Data);
                        var j = doc.RootElement;
                        usage = new AcpUsage(j.GetProperty("input_tokens").GetInt32(), j.GetProperty("output_tokens").GetInt32(), j.GetProperty("total_tokens").GetInt32());
                    }
                    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                    {
                        // A malformed usage frame is ignored.
                    }
                    break;
                case "error":
                    error = Field(e.Data, "message");
                    break;
                case "done":
                    done = true;
                    break;
                default:
                    pause ??= AcpPause.From(e);
                    break;
            }
        }
        int n;
        while ((n = await reader.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false)) > 0)
            foreach (var e in parser.Feed(new string(buf, 0, n))) Handle(e);
        if (parser.Flush() is { } last) Handle(last);

        var end = pause is not null ? TurnEnd.Paused : error is not null ? TurnEnd.Error : done ? TurnEnd.Done : TurnEnd.StreamCut;
        return new AcpTurnResult(end, text.ToString(), pause, error is null ? null : Common.Redactor.Shared.Redact(error), usage, tools);
    }

    private static string Field(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
