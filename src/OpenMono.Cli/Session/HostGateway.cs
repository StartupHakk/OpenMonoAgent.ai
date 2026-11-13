using System.Net.Http;
using OpenMono.HostBridge;

namespace OpenMono.Session;

/// <summary>
/// The result of one gateway run: whether the host command actually executed,
/// whether it was refused (and why), and the text answer for the model loop.
/// Callers map this onto tool results so a failed host action renders as a
/// failure (✗) instead of a success (✓) — the old bare-string return hid
/// every refusal class behind the same "looks like an answer" shape.
/// </summary>
public sealed record HostExecOutcome(
    bool Ok,
    bool Denied,
    string? ErrorCode,
    string Output)
{
    /// <summary>The text fed to the model (execution output or refusal
    /// message) — unchanged wording from before the typed outcome.</summary>
    public string Answer => Output;

    public const string ErrStale = "host_session_stale";
    public const string ErrPolicy = "host_policy_denied";
    public const string ErrUnreachable = "daemon_unreachable";
    public const string ErrUserDenied = "user_denied";
    public const string ErrExecFailed = "host_execution_failed";
    // The daemon rejected the per-exec sudo password; the client may re-prompt
    // (long-lived consent modal) rather than surface this to the model yet.
    public const string ErrPasswordFailure = "sudo_password_failure";
}

/// <summary>
/// The sandbox side of host mode: pairs with a bare-metal host-bridge daemon
/// and runs user-approved HOST_EXEC commands on it. The model loop lives in
/// the container and never touches the host directly — every host action is
/// (1) consented by the human in their UI (TUI card / ACP extension answer),
/// then (2) relayed to the daemon, which re-enforces the deny-list + sudo
/// policy as its last line of defense and audits everything.
///
/// Session grants mirror the operator bridge's permission store: once the
/// user answers "allow all", further commands run without re-prompting until
/// the pairing is revoked. Escalations (as_root) always re-prompt — allow-all
/// never covers sudo. The consent vocabulary is shared through
/// <see cref="HostConsent"/> so every surface means the same thing.
///
/// Long-running sessions: while this process lives, a background keepalive
/// heartbeat refreshes the daemon's idle clock, so idle-token expiry becomes
/// an orphan-GC for dead agents rather than an ambush on live ones. A pairing
/// that still goes stale (daemon restart, operator revoke, hard cap) is
/// silently re-paired once through the daemon's pair_auto lane and the
/// refused exec is retried once — safe because the refused exec never ran.
/// Security is unchanged: the daemon restart still wipes every token, an
/// unknown session still cannot pair without a credential or operator
/// approval, and <c>pair_auto=strict</c> disables the silent re-pair.
/// </summary>
public sealed class HostGateway : IDisposable
{
    private readonly HostDaemonClient _client;
    private readonly string _sessionId;

    private string? _token;
    private readonly string? _invite;
    private DateTime? _grantUntilUtc; // null = no blanket grant; DateTime.MaxValue = whole session
    private bool _denyAll;
    private SudoPolicy _sudo = SudoPolicy.Ask;
    private System.Threading.Timer? _heartbeat;
    private readonly SemaphoreSlim _repairGate = new(1, 1);
    private const int HeartbeatDefaultMinutes = 30;
    private const int HeartbeatFloorSeconds = 60;
    private volatile bool _disposed;
    // Test seam: force the heartbeat cadence (set before pairing).
    internal TimeSpan? HeartbeatIntervalOverride { get; set; }

    public string BaseUrl { get; }
    public string? RunAs { get; private set; }
    public string? HostLogDir { get; private set; }
    public string WorkDir { get; }
    public bool IsPaired => _token is not null;
    public bool SessionAllowsAll => _grantUntilUtc is { } until && DateTime.UtcNow < until;
    public bool SessionDeniesAll => _denyAll;
    public SudoPolicy Sudo => _sudo;
    public string? PairSecret { get; }

    public HostGateway(string baseUrl, string sessionId, string workDir, string? pairSecret = null, string? invite = null)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _client = new HostDaemonClient(BaseUrl);
        _sessionId = sessionId;
        WorkDir = workDir;
        PairSecret = pairSecret;
        _invite = invite;
    }

    /// <summary>
    /// Pair with the daemon using the shared secret. Returns null on success
    /// or a human-readable failure reason. Re-pair rotates the token, so a
    /// second call cleanly replaces a stale pairing.
    /// </summary>
    public async Task<string?> PairAsync(string? secretOverride = null, CancellationToken ct = default)
    {
        var secret = secretOverride ?? PairSecret;
        // No secret and no invite is not an error: the daemon treats it as a
        // pair *request* the operator approves on the host (openmono host
        // approve, or the [y/N] prompt on a foreground daemon).

        var ping = await _client.PingAsync(ct);
        if (!ping.Daemon)
        {
            // Three distinct failures hide behind "daemon not here": nothing
            // listening, a route that answers but is not the daemon (404
            // prefix mismatch, port collision), and a daemon that is down.
            // Saying which one we see is what makes the error actionable.
            return ping.Reachable
                ? $"something answers HTTP at {BaseUrl} but it is not the host-bridge daemon " +
                  "(wrong port, or the request is being answered by another service)"
                : $"host daemon not reachable at {BaseUrl} — start it on the host machine: openmono host";
        }

        var resp = await _client.PairAsync(new PairRequest(secret ?? "", _sessionId, WorkDir, _invite), ct);
        if (!resp.Ok || string.IsNullOrEmpty(resp.Token))
            return resp.Error switch
            {
                "bad_secret" => "pairing was refused (bad pair secret) — check ~/.openmono/host-bridge.json on the host",
                "pair_denied" => "the operator on the host denied the pairing request",
                "pair_timeout" => "the operator on the host did not approve the pairing request in time — ask them to run: openmono host requests",
                "daemon unreachable" => $"host daemon went down at {BaseUrl}",
                _ => resp.Error ?? "pairing failed",
            };

        _token = resp.Token;
        RunAs = resp.RunAs;
        HostLogDir = resp.LogDir;
        _sudo = resp.Sudo switch
        {
            "allow" => SudoPolicy.Allow,
            "deny" => SudoPolicy.Deny,
            _ => SudoPolicy.Ask,
        };
        _grantUntilUtc = null;
        _denyAll = false;
        await StartHeartbeatAsync(ct);
        return null;
    }

    /// <summary>
    /// Heartbeat the daemon's idle clock so a live agent's pairing never
    /// expires out from under it. The interval is sized to this daemon's
    /// own idle window (one third of it, clamped to 1m..30m) so the cadence
    /// adapts to aggressive <c>--idle-timeout</c> settings; old daemons that
    /// do not report the window get the 30m default, and a daemon with
    /// expiry disabled (<c>--idle-timeout 0</c>, reported as -1) needs no
    /// heartbeat at all.
    /// </summary>
    private async Task StartHeartbeatAsync(CancellationToken ct)
    {
        StopHeartbeat();
        if (_disposed) return;

        // PairAsync just succeeded, so this round trip is cheap; StatusAsync
        // never throws (unreachable daemon = Daemon=false = skip — the next
        // exec's auto-repair re-pairs and re-arms the heartbeat).
        var status = await _client.StatusAsync(ct);
        if (_disposed || !status.Daemon || status.IdleTimeoutSeconds < 0)
            return; // no expiry configured — nothing to keep alive

        var idleSeconds = status.IdleTimeoutSeconds > 0
            ? status.IdleTimeoutSeconds
            : HeartbeatDefaultMinutes * 60; // old daemon without the field
        var intervalSeconds = HeartbeatIntervalSeconds(idleSeconds);

        _heartbeat = new System.Threading.Timer(
            _ => _ = HeartbeatOnceAsync(),
            null,
            HeartbeatIntervalOverride ?? TimeSpan.FromSeconds(intervalSeconds),
            HeartbeatIntervalOverride ?? TimeSpan.FromSeconds(intervalSeconds));
    }

    /// <summary>Heartbeat cadence for a daemon reporting the given idle
    /// window in seconds (-1 = expiry disabled → no heartbeat). One third of
    /// the window, clamped to 1m..30m.</summary>
    internal static int HeartbeatIntervalSeconds(int reportedIdleSeconds)
    {
        if (reportedIdleSeconds < 0)
            return -1;
        var idle = reportedIdleSeconds > 0
            ? reportedIdleSeconds
            : HeartbeatDefaultMinutes * 60;
        return Math.Clamp(idle / 3, HeartbeatFloorSeconds, HeartbeatDefaultMinutes * 60);
    }

    private async Task HeartbeatOnceAsync()
    {
        var token = _token;
        if (_disposed || token is null) return;
        try
        {
            var resp = await _client.KeepaliveAsync(new RevokeRequest(token, _sessionId), CancellationToken.None);
            // "daemon unreachable" (old daemon without /keepalive, or a
            // blip): keep the timer running — exec's auto-repair covers
            // a genuinely dead pairing. An expired/dropped pairing stops
            // the heartbeat; the next exec re-pairs through the repair
            // path, which restarts it.
            if (!resp.Ok && resp.Error is "token_expired" or "no_such_session_or_token")
                StopHeartbeat();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ObjectDisposedException)
        {
            // Swallowed: the next beat or the next exec's auto-repair
            // handles recovery. Never let a timer callback throw.
        }
    }

    private void StopHeartbeat()
    {
        var timer = Interlocked.Exchange(ref _heartbeat, null);
        timer?.Dispose();
    }

    /// <summary>
    /// True when the daemon side of THIS session's pairing is still alive.
    /// Tokens live only in the daemon's memory, so a daemon restart silently
    /// invalidates every pairing while IsPaired (a local flag) stays true —
    /// the chip then reads HOST:ON and /host on would no-op against a dead
    /// token. The check is per-token (/verify): the old global paired-count
    /// lied when another session was still paired — this session's dead token
    /// read "live" and every exec came back unpaired_or_bad_token. Two cases
    /// still count as live so we never revoke a pairing on a network blip or
    /// an old daemon without /verify: unreachable daemon, and daemon that
    /// answered without the route. Exec fails closed either way.
    /// </summary>
    public async Task<bool> IsPairingLiveAsync(CancellationToken ct = default)
    {
        if (_token is null)
            return false;
        var verify = await _client.VerifyAsync(new RevokeRequest(_token, _sessionId), ct);
        if (!verify.Daemon)
            return true;
        return verify.Live;
    }

    /// <summary>Drop the pairing on the daemon and locally. Idempotent.</summary>
    public async Task UnpairAsync(CancellationToken ct = default)
    {
        StopHeartbeat();
        if (_token is not null)
        {
            try { await _client.RevokeAsync(new RevokeRequest(_token, _sessionId), ct); }
            catch { /* daemon gone — the token dies with it */ }
        }
        _token = null;
        RunAs = null;
        HostLogDir = null;
        _grantUntilUtc = null;
        _denyAll = false;
    }

    /// <summary>
    /// Verify a candidate sudo password on the host without running anything
    /// (daemon route /sudo/verify). Part of the elevation consent flow: the
    /// surface collects the password in its card, verifies it here, retries
    /// on auth failure, and only hands the VERIFIED password to the
    /// subsequent <see cref="RunAsync"/> for the real command. The password
    /// rides this one request and is wiped on every return path — never
    /// logged, cached, or persisted.
    /// </summary>
    public async Task<SudoVerifyResponse> VerifySudoPasswordAsync(bool asRoot, string? password, CancellationToken ct)
    {
        if (_token is null)
            return new SudoVerifyResponse(false, Denied: true, Error: "unpaired_or_bad_token",
                Output: "HOST EXECUTION refused: this session is not paired with a host daemon (/host on).");
        return await _client.SudoVerifyAsync(new SudoVerifyRequest(_token, _sessionId, asRoot, password ?? ""), ct);
    }

    /// <summary>
    /// Run one HOST_EXEC request through consent + daemon relay. Never throws
    /// on policy/denial — every refusal comes back as a typed
    /// <see cref="HostExecOutcome"/> carrying the same textual answers the
    /// operator bridge produces, so callers can render an honest failure.
    /// <paramref name="consent"/> is the surface-specific human prompt.
    /// </summary>
    /// <param name="sudoPassword">
    /// One-shot sudo password from the client consent card (extension path).
    /// Carried to the daemon for THIS exec only, wiped on every return path —
    /// never cached, logged, or persisted.
    /// </param>
    public async Task<HostExecOutcome> RunAsync(
        HostExecRequest request,
        Func<string, bool, CancellationToken, Task<HostConsentDecision>> consent,
        CancellationToken ct,
        string? sudoPassword = null)
    {
        if (_token is null)
            return new HostExecOutcome(false, false, HostExecOutcome.ErrStale,
                "HOST EXECUTION refused: this session is not paired with a host daemon (/host on).");

        if (request.AsRoot && _sudo == SudoPolicy.Deny)
            return new HostExecOutcome(false, true, HostExecOutcome.ErrPolicy,
                "Host execution refused: as_root was requested but sudo is denied on this host. " +
                "Re-send without \"as_root\": true, or ask the user to enable sudo (openmono config host.sudo_policy allow).");

        var decision = HostConsentDecision.AllowOnce;
        if (_denyAll)
            decision = HostConsentDecision.DenyAll;
        else if (!(_grantUntilUtc is { } until && DateTime.UtcNow < until && !request.AsRoot))
            decision = await consent(request.Command, request.AsRoot, ct);

        switch (decision)
        {
            case HostConsentDecision.AllowAll:
                _grantUntilUtc = DateTime.MaxValue;
                break;
            case HostConsentDecision.Allow30m:
                _grantUntilUtc = DateTime.UtcNow.AddMinutes(30);
                break;
            case HostConsentDecision.AllowToday:
                _grantUntilUtc = DateTime.UtcNow.Date.AddDays(1);
                break;
            case HostConsentDecision.DenyAll:
                _denyAll = true;
                return new HostExecOutcome(false, true, HostExecOutcome.ErrUserDenied,
                    $"Host execution denied by user (host mode off for this session): {request.Command}");
            case HostConsentDecision.DenyOnce:
                return new HostExecOutcome(false, true, HostExecOutcome.ErrUserDenied,
                    $"Host execution denied by user: {request.Command}");
        }

        var token = _token;
        try
        {
            var resp = await _client.ExecAsync(new ExecRequest(token!, _sessionId, request, sudoPassword), ct);

            // Stale pairing self-heal: the refused exec never ran, so retrying
            // after a silent re-pair is safe. One flight at a time so parallel
            // execs cannot race token rotations against each other.
            if (resp.Denied && resp.Error == "unpaired_or_bad_token")
            {
                resp = await RepairAndRetryAsync(token!, request, ct, sudoPassword);
            }

            if (resp.Denied && resp.Error is not null)
            {
                if (resp.Error == "unpaired_or_bad_token")
                    return new HostExecOutcome(false, false, HostExecOutcome.ErrStale,
                        "HOST EXECUTION refused by the host daemon: the session pairing is stale (token rotated or revoked). Re-pair with /host on.");
                if (resp.Error == "daemon unreachable")
                    return new HostExecOutcome(false, false, HostExecOutcome.ErrUnreachable,
                        $"HOST EXECUTION refused: the host daemon at {BaseUrl} is unreachable (is it still running?). No host command ran.");
                if (resp.Error == "password_failure")
                {
                    var target = request.AsRoot ? "root" : "the target user";
                    return new HostExecOutcome(false, true, HostExecOutcome.ErrPasswordFailure,
                        $"sudo authentication failed for {target}: the password was wiped on the host. The user may retry from the consent card: {request.Command}");
                }
                return new HostExecOutcome(false, true, HostExecOutcome.ErrPolicy,
                    $"Host execution denied by host-bridge policy: {request.Command}");
            }

            if (resp.Denied)
            {
                // Denied without an error code = the daemon ran the policy gate
                // (deny-list / sudo refusal) or the executor itself refused
                // (timeout, sudo password missing, launch failure). The message
                // text already says which; split by shape so tool results can
                // carry a precise error code.
                var policyDenied = resp.Output.StartsWith("Host execution denied by host-bridge policy", StringComparison.Ordinal);
                return new HostExecOutcome(false, policyDenied,
                    policyDenied ? HostExecOutcome.ErrPolicy : HostExecOutcome.ErrExecFailed,
                    resp.Output);
            }

            var targetUser = request.AsRoot ? "root" : RunAs ?? "host user";
            var head = $"[host-exec on {WorkDir} as {targetUser}]";
            return new HostExecOutcome(true, false, null, $"{head}\n$ {request.Command}\n{resp.Output}");
        }
        finally
        {
            ZeroPassword(sudoPassword);
        }
    }

    /// <summary>Zero the char copy of a per-exec password. The string itself
    /// escapes our control once handed to the serializer; the copy is what we
    /// can guarantee is wiped on every return path.</summary>
    private static void ZeroPassword(string? password)
    {
        if (string.IsNullOrEmpty(password)) return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(password);
        try
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
        catch
        {
            // Zeroing a copy is best-effort hygiene, never a failure path.
        }
    }

    /// <summary>
    /// Silent re-pair + one retry after the daemon refused a stale token.
    /// Credential-free auto_only lane: the daemon only re-approves a session
    /// this run already paired (pair_auto=this_session), and fails fast —
    /// never an operator prompt — otherwise. An idle expiry is therefore
    /// self-healed invisibly; an operator revoke or a hard-cap expiry is
    /// not (the daemon cleared its auto-approve memory for those), so the
    /// exec comes back with the original fail-closed answer.
    /// Safe to retry because a refused exec never ran.
    /// </summary>
    private async Task<ExecResponse> RepairAndRetryAsync(string deadToken, HostExecRequest request, CancellationToken ct, string? sudoPassword = null)
    {
        await _repairGate.WaitAsync(ct);
        try
        {
            // Another exec may already have repaired this pairing while we
            // queued — only re-pair if our token is still the current one.
            if (_token == deadToken)
            {
                var repaired = await TryAutoRepairAsync(ct);
                if (!repaired)
                    return new ExecResponse(false, Denied: true, Error: "unpaired_or_bad_token");
            }
            if (_token is null)
                return new ExecResponse(false, Denied: true, Error: "unpaired_or_bad_token");
            return await _client.ExecAsync(new ExecRequest(_token, _sessionId, request, sudoPassword), ct);
        }
        finally
        {
            _repairGate.Release();
        }
    }

    /// <summary>The daemon-side auto lane for stale-token repair (no secret,
    /// no invite, auto_only=true). Never revokes the session's consent
    /// grants: allow-all/deny-all are user decisions, not token decisions.
    /// Returns true when the pairing was re-minted.</summary>
    private async Task<bool> TryAutoRepairAsync(CancellationToken ct)
    {
        var ping = await _client.PingAsync(ct);
        if (!ping.Daemon)
            return false;
        var resp = await _client.PairAsync(
            new PairRequest("", _sessionId, WorkDir, Invite: null, AutoOnly: true), ct);
        if (!resp.Ok || string.IsNullOrEmpty(resp.Token))
            return false;
        _token = resp.Token;
        RunAs = resp.RunAs;
        HostLogDir = resp.LogDir;
        _sudo = resp.Sudo switch
        {
            "allow" => SudoPolicy.Allow,
            "deny" => SudoPolicy.Deny,
            _ => SudoPolicy.Ask,
        };
        await StartHeartbeatAsync(ct);
        return true;
    }

    /// <summary>
    /// The standing-orders preamble for this pairing (null when unpaired).
    /// Injected into the system prompt when host mode activates so the model
    /// knows its own tools see only the container workspace.
    /// </summary>
    public string? BuildStandingOrders() => IsPaired
        ? HostStandingOrders.Build(WorkDir, RunAs ?? "host user", HostLogDir ?? "~/.openmono/logs", _sudo)
        : null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopHeartbeat();
        _repairGate.Dispose();
        _client.Dispose();
    }
}
