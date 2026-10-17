using OpenMono.HostBridge;

namespace OpenMono.Session;

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
/// </summary>
public sealed class HostGateway
{
    private readonly HostDaemonClient _client;
    private readonly string _sessionId;

    private string? _token;
    private readonly string? _invite;
    private DateTime? _grantUntilUtc; // null = no blanket grant; DateTime.MaxValue = whole session
    private bool _denyAll;
    private SudoPolicy _sudo = SudoPolicy.Ask;

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
        return null;
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
    /// Run one HOST_EXEC request through consent + daemon relay and return the
    /// text answer for the model loop. Never throws on policy/denial — those
    /// come back as textual answers, mirroring the operator bridge exactly.
    /// <paramref name="consent"/> is the surface-specific human prompt.
    /// </summary>
    public async Task<string> RunAsync(
        HostExecRequest request,
        Func<string, bool, CancellationToken, Task<HostConsentDecision>> consent,
        CancellationToken ct)
    {
        if (_token is null)
            return "HOST EXECUTION refused: this session is not paired with a host daemon (/host on).";

        if (request.AsRoot && _sudo == SudoPolicy.Deny)
            return "Host execution refused: as_root was requested but sudo is denied on this host. " +
                "Re-send without \"as_root\": true, or ask the user to enable sudo (openmono config host.sudo_policy allow).";

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
                return $"Host execution denied by user (host mode off for this session): {request.Command}";
            case HostConsentDecision.DenyOnce:
                return $"Host execution denied by user: {request.Command}";
        }

        var resp = await _client.ExecAsync(new ExecRequest(_token, _sessionId, request), ct);
        if (resp.Denied && resp.Error is not null)
        {
            if (resp.Error == "unpaired_or_bad_token")
                return "HOST EXECUTION refused by the host daemon: the session pairing is stale (token rotated or revoked). Re-pair with /host on.";
            return $"Host execution denied by host-bridge policy: {request.Command}";
        }

        var targetUser = request.AsRoot ? "root" : RunAs ?? "host user";
        var head = $"[host-exec on {WorkDir} as {targetUser}]";
        return $"{head}\n$ {request.Command}\n{resp.Output}";
    }

    /// <summary>
    /// The standing-orders preamble for this pairing (null when unpaired).
    /// Injected into the system prompt when host mode activates so the model
    /// knows its own tools see only the container workspace.
    /// </summary>
    public string? BuildStandingOrders() => IsPaired
        ? HostStandingOrders.Build(WorkDir, RunAs ?? "host user", HostLogDir ?? "~/.openmono/logs", _sudo)
        : null;
}
