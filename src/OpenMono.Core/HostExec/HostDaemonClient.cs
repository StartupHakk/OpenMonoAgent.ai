using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenMono.HostBridge;

/// <summary>
/// Wire contract between a sandbox agent (container) and a host-bridge daemon
/// (bare metal). The daemon listens on loopback; the container reaches it via
/// <c>host.docker.internal</c>. Requests carry a session token handed out by
/// <c>pair</c> — without a live token the daemon fails closed.
///
/// Shapes (one record each, so client and server can never drift):
///   POST /pair    { secret, session_id, workdir }      -> PairResponse
///   POST /exec    { token, session_id, request }       -> ExecResponse
///   POST /revoke  { token, session_id }                -> StatusResponse
///   POST /verify  { token, session_id }                -> VerifyResponse
///   GET  /ping                                         -> PingResponse
/// </summary>
public sealed record PairRequest(
    [property: JsonPropertyName("secret")] string Secret,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("workdir")] string WorkDir,
    [property: JsonPropertyName("invite")] string? Invite = null);

public sealed record PairResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("token")] string? Token = null,
    [property: JsonPropertyName("run_as")] string? RunAs = null,
    [property: JsonPropertyName("sudo")] string? Sudo = null,
    [property: JsonPropertyName("log_dir")] string? LogDir = null,
    [property: JsonPropertyName("error")] string? Error = null,
    [property: JsonPropertyName("reason")] string? Reason = null);

public sealed record ExecRequest(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("request")] HostExecRequest Request);

public sealed record ExecResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("output")] string Output = "",
    [property: JsonPropertyName("denied")] bool Denied = false,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record RevokeRequest(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("session_id")] string SessionId);

public sealed record StatusResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record VerifyResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("daemon")] bool Daemon,
    // True only when THIS session+token pair still matches a live pairing.
    // The global /status paired-count cannot say that: a dead token next to
    // another session's live pairing reads "paired" there and lies here.
    [property: JsonPropertyName("live")] bool Live = false);

public sealed record PingResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("daemon")] bool Daemon,
    [property: JsonPropertyName("version")] string Version = "",
    // True when something answered over HTTP, even with the wrong body. Lets
    // the caller tell "no route to the daemon" (404 prefix mismatch, a proxy,
    // a port collision) apart from "nothing is listening".
    [property: JsonPropertyName("reachable")] bool Reachable = false);

public sealed record DaemonStatusResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("daemon")] bool Daemon,
    [property: JsonPropertyName("port")] int Port = 0,
    [property: JsonPropertyName("version")] string Version = "",
    [property: JsonPropertyName("paired_sessions")] int PairedSessions = 0);

// ── Admin surface (host-side CLI only; authenticated by the pair secret) ──
public sealed record InviteRequest(
    [property: JsonPropertyName("session_id")] string SessionId,
    // Optional per-invite TTL override (the daemon clamps it to 30s..24h).
    // The launcher uses a long TTL for manual /host on sessions.
    [property: JsonPropertyName("ttl_seconds")] int? TtlSeconds = null);

public sealed record InviteResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("invite")] string? Invite = null,
    [property: JsonPropertyName("error")] string? Error = null);

public sealed record ApproveRequest(
    [property: JsonPropertyName("session_id")] string SessionId);

public sealed record PairedSessionInfo(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("workdir")] string WorkDir,
    [property: JsonPropertyName("token_hint")] string TokenHint,
    [property: JsonPropertyName("since")] string Since);

public sealed record AdminListResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("items")] List<PairedSessionInfo>? Items = null,
    [property: JsonPropertyName("error")] string? Error = null);

/// <summary>
/// Thin HTTP client for a host-bridge daemon. Used by the sandbox agent's host
/// gateway (/host on) to pair and to run approved host commands on bare metal.
/// No retries: a failed call is a failed host command, surfaced verbatim.
/// </summary>
public sealed class HostDaemonClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string? _hostHeaderOverride;

    public string BaseUrl { get; }

    public HostDaemonClient(string baseUrl)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(11) };
        _hostHeaderOverride = ResolveHostHeaderOverride(BaseUrl);
    }

    /// <summary>
    /// The daemon always binds loopback (http://127.0.0.1:&lt;port&gt;/) plus any
    /// detected VM bridge IPs, so whole-machine exec is never exposed to the
    /// LAN. Requests from inside a container reach it through Docker's
    /// host.docker.internal gateway, but the Host: header then names the
    /// gateway, which HttpListener 404s before the request ever reaches the
    /// daemon — on every route: Docker Desktop's vpnkit forwards to host
    /// loopback, and Colima/Lima dial a vmnet bridge IP. Rewrite the header
    /// to the loopback form — 127.0.0.1 is a bound prefix on both runtimes,
    /// so the request is accepted however it was routed. TCP path unchanged.
    /// </summary>
    internal static string? ResolveHostHeaderOverride(string baseUrl)
    {
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            && uri.Host.Equals("host.docker.internal", StringComparison.OrdinalIgnoreCase))
        {
            var defaultPort = uri.Scheme == Uri.UriSchemeHttps ? 443 : 80;
            return uri.Port == defaultPort ? "127.0.0.1" : $"127.0.0.1:{uri.Port}";
        }
        return null;
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, $"{BaseUrl}{path}");
        if (_hostHeaderOverride is not null)
            req.Headers.Host = _hostHeaderOverride;
        return req;
    }

    public async Task<PingResponse> PingAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.SendAsync(NewRequest(HttpMethod.Get, "/ping"), HttpCompletionOption.ResponseContentRead, ct);
            // Any HTTP answer means the port is served, even a 404 from an
            // HttpListener prefix mismatch or a different service entirely.
            // That is a broken route, not a dead daemon; callers need to say
            // which one they are looking at.
            if (!resp.IsSuccessStatusCode)
                return new PingResponse(false, Daemon: false, Reachable: true);
            var ping = await ReadJsonAsync<PingResponse>(resp, ct);
            if (ping is null || !ping.Daemon)
                return new PingResponse(false, Daemon: false, Reachable: true);
            return ping with { Reachable = true };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Nothing answered at the TCP/HTTP level: refused, no route, or
            // the connect timed out.
            return new PingResponse(false, Daemon: false, Reachable: false);
        }
    }

    public async Task<DaemonStatusResponse> StatusAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.SendAsync(NewRequest(HttpMethod.Get, "/status"), HttpCompletionOption.ResponseContentRead, ct);
            return await ReadJsonAsync<DaemonStatusResponse>(resp, ct) ?? new DaemonStatusResponse(false, Daemon: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new DaemonStatusResponse(false, Daemon: false);
        }
    }

    public Task<PairResponse> PairAsync(PairRequest request, CancellationToken ct) =>
        PostJsonAsync<PairRequest, PairResponse>("/pair", request, ct,
            new PairResponse(false, Error: "daemon unreachable"));

    public Task<ExecResponse> ExecAsync(ExecRequest request, CancellationToken ct) =>
        PostJsonAsync<ExecRequest, ExecResponse>("/exec", request, ct,
            new ExecResponse(false, Output: "", Denied: true, Error: "daemon unreachable"));

    public Task<StatusResponse> RevokeAsync(RevokeRequest request, CancellationToken ct) =>
        PostJsonAsync<RevokeRequest, StatusResponse>("/revoke", request, ct,
            new StatusResponse(false, Error: "daemon unreachable"));

    /// <summary>Ask the daemon whether this exact session+token is still a
    /// live pairing. Old daemons answer 404 (not found), which deserializes to
    /// Live=false with Daemon=false — callers must treat "daemon answered but
    /// no /verify route" as unknown, not dead.</summary>
    public Task<VerifyResponse> VerifyAsync(RevokeRequest request, CancellationToken ct) =>
        PostJsonAsync<RevokeRequest, VerifyResponse>("/verify", request, ct,
            new VerifyResponse(false, Daemon: false));

    // ── Admin surface (host-side CLI; pair secret in a header) ──
    // Pair requests wait on the operator for up to 2 daemon minutes, so the
    // admin HTTP timeout is longer than the exec timeout.
    private readonly Lazy<HttpClient> _admin = new(() =>
        new HttpClient { Timeout = TimeSpan.FromMinutes(4) });

    private HttpRequestMessage AdminRequest(HttpMethod method, string path, string pairSecret)
    {
        var req = NewRequest(method, path);
        req.Headers.Remove("Host");
        req.Headers.TryAddWithoutValidation("X-Pair-Secret", pairSecret);
        return req;
    }

    public async Task<InviteResponse> CreateInviteAsync(string pairSecret, string sessionId, CancellationToken ct, int? ttlSeconds = null)
    {
        try
        {
            using var req = AdminRequest(HttpMethod.Post, "/admin/invite", pairSecret);
            req.Content = new StringContent(
                JsonSerializer.Serialize(new InviteRequest(sessionId, ttlSeconds)), Encoding.UTF8, "application/json");
            using var resp = await _admin.Value.SendAsync(req, ct);
            return await ReadJsonAsync<InviteResponse>(resp, ct)
                ?? new InviteResponse(false, Error: "daemon unreachable");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new InviteResponse(false, Error: "daemon unreachable");
        }
    }

    public async Task<AdminListResponse> AdminListAsync(string pairSecret, string path, CancellationToken ct)
    {
        try
        {
            using var req = AdminRequest(HttpMethod.Get, path, pairSecret);
            using var resp = await _admin.Value.SendAsync(req, ct);
            return await ReadJsonAsync<AdminListResponse>(resp, ct)
                ?? new AdminListResponse(false, Error: "daemon unreachable");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new AdminListResponse(false, Error: "daemon unreachable");
        }
    }

    public async Task<StatusResponse> AdminActionAsync(string pairSecret, string path, string sessionId, CancellationToken ct)
    {
        try
        {
            using var req = AdminRequest(HttpMethod.Post, path, pairSecret);
            req.Content = new StringContent(
                JsonSerializer.Serialize(new ApproveRequest(sessionId)), Encoding.UTF8, "application/json");
            using var resp = await _admin.Value.SendAsync(req, ct);
            return await ReadJsonAsync<StatusResponse>(resp, ct)
                ?? new StatusResponse(false, Error: "daemon unreachable");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new StatusResponse(false, Error: "daemon unreachable");
        }
    }

    private async Task<TResp> PostJsonAsync<TReq, TResp>(string path, TReq body, CancellationToken ct, TResp fallback)
        where TResp : class
    {
        try
        {
            var json = JsonSerializer.Serialize(body);
            using var req = NewRequest(HttpMethod.Post, path);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);
            return await ReadJsonAsync<TResp>(resp, ct) ?? fallback;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return fallback;
        }
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage resp, CancellationToken ct)
        where T : class
    {
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text))
            return null;
        try
        {
            return JsonSerializer.Deserialize<T>(text, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public void Dispose() => _http.Dispose();
}
