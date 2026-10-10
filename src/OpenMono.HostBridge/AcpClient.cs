using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace OpenMono.HostBridge;

/// <summary>
/// One server-sent event from an ACP turn stream.
/// Framing (see SseWriter): <c>event: {name}\ndata: {json}\n\n</c>.
/// </summary>
public sealed record AcpEvent(string Type, JsonElement Data);

/// <summary>
/// Minimal HTTP client for the in-container agent's ACP API — the same API the
/// VS Code extension and acp-smoke.sh use. No protocol changes: sessions, turns,
/// pauses, diffs are all stock.
/// </summary>
public sealed class AcpClient : IDisposable
{
    private readonly HttpClient _http;
    private bool _disposed;

    public AcpClient()
    {
        _http = new HttpClient
        {
            // Turn streams stay open for the whole model loop + tool execution.
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<JsonElement> GetDiscoveryAsync(string baseUrl, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"{baseUrl}/api/v1/discovery", ct);
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct);
        return doc is null ? throw new InvalidOperationException("empty discovery response") : doc.RootElement.Clone();
    }

    public async Task<string> CreateSessionAsync(string baseUrl, string? model, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync(
            $"{baseUrl}/api/v1/sessions", new { model }, ct);
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct)
            ?? throw new InvalidOperationException("empty session response");
        return doc.RootElement.GetProperty("session_id").GetString()
            ?? throw new InvalidOperationException("session response missing session_id");
    }

    public async Task EnsureSessionAsync(string baseUrl, string sessionId, CancellationToken ct)
    {
        using var res = await _http.GetAsync($"{baseUrl}/api/v1/sessions/{sessionId}", ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException($"ACP session not found: {sessionId}");
        res.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Sets the session mode (<c>plan</c> read-only vs <c>build</c> can-act) via
    /// the stock turn endpoint. Plain JSON reply, no SSE. The bridge puts host
    /// sessions in build mode unless <c>--plan</c> was given — new ACP sessions
    /// default to plan, which cannot run deployments.
    /// </summary>
    public async Task<string> SetModeAsync(string baseUrl, string sessionId, string mode, CancellationToken ct)
    {
        using var res = await _http.PostAsJsonAsync(
            $"{baseUrl}/api/v1/sessions/{sessionId}/turn", new { mode }, ct);
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct)
            ?? throw new InvalidOperationException("empty mode response");
        return doc.RootElement.GetProperty("mode").GetString() ?? mode;
    }

    /// <summary>
    /// POST a turn body ({message} or a pause resume) and stream the SSE reply.
    /// The stream ends at <c>done</c>/<c>error</c>, or stays open-but-idle after
    /// a pause event — the caller resolves the pause with a fresh POST and
    /// pumps that stream instead (same pattern the extension follows).
    /// </summary>
    public async IAsyncEnumerable<AcpEvent> PostTurnStreamAsync(
        string baseUrl,
        string sessionId,
        object payload,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, BridgeConfig.WebOptions());
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/v1/sessions/{sessionId}/turn")
        {
            Content = content,
        };
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"turn POST failed ({(int)res.StatusCode}): {Truncate(body, 500)}");
        }

        var stream = await res.Content.ReadAsStreamAsync(ct);
        await foreach (var evt in ReadSseAsync(stream, ct))
            yield return evt;
    }

    public static object PermissionPayload(string id, bool allow, string scope) =>
        new Dictionary<string, object>
        {
            ["permission"] = new Dictionary<string, object>
            {
                ["id"] = id,
                ["decision"] = allow ? "allow" : "deny",
                ["scope"] = scope,
            },
        };

    public static object UserInputPayload(string id, string value) =>
        new Dictionary<string, object>
        {
            ["user_input"] = new Dictionary<string, object>
            {
                ["id"] = id,
                ["value"] = value,
            },
        };

    public static object ToggleModePayload(string id, bool approve) =>
        new Dictionary<string, object>
        {
            ["toggle_mode"] = new Dictionary<string, object>
            {
                ["id"] = id,
                ["decision"] = approve ? "approve" : "deny",
            },
        };

    public static object PlaybookPayload(string id, bool allow) =>
        new Dictionary<string, object>
        {
            ["playbookPermission"] = new Dictionary<string, object>
            {
                ["id"] = id,
                ["decision"] = allow ? "allow" : "deny",
            },
        };

    internal static async IAsyncEnumerable<AcpEvent> ReadSseAsync(
        Stream stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? eventName = null;
        var data = new StringBuilder();

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                yield break;
            if (line.Length == 0)
            {
                if (eventName is not null)
                {
                    JsonElement dataEl;
                    try
                    {
                        using var doc = JsonDocument.Parse(
                            data.Length == 0 ? "{}" : data.ToString());
                        dataEl = doc.RootElement.Clone();
                    }
                    catch (JsonException)
                    {
                        dataEl = JsonDocument.Parse("{}").RootElement.Clone();
                    }
                    yield return new AcpEvent(eventName, dataEl);
                }
                eventName = null;
                data.Clear();
                continue;
            }
            if (line.StartsWith("event:", StringComparison.Ordinal))
                eventName = line["event:".Length..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                    data.Append('\n');
                data.Append(line["data:".Length..].TrimStart(' '));
            }
        }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s[..max] + "...";

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _http.Dispose();
    }
}
