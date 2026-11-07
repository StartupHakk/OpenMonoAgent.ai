using System.Net;
using System.Text.Json;

namespace OpenMono.HostBridge;

/// <summary>
/// Bare-metal host daemon: the bare-metal half of host mode when the model
/// loop lives in a sandbox container (TUI or ACP/extension), not the operator
/// bridge. It listens on LOOPBACK and, on Linux, additionally on the local
/// docker bridge gateway (docker0 / br-*) so containers can reach it via
/// <c>host.docker.internal</c> (docker-compose sets <c>host-gateway</c>).
/// The daemon never binds 0.0.0.0 or a wildcard: it stays unreachable from
/// any LAN/WAN interface.
///
/// Trust model (defense in depth):
///  - Pairing requires the shared <c>pair_secret</c> (0600 config / env).
///    A random container that lacks it cannot pair. This is a guardrail, not a
///    sandbox boundary.
///  - Pairing mints a random in-memory session token. Every exec must carry it
///    AND the session id it was issued for; a mismatch fails closed. Revoking
///    (client /host off, or daemon stop) drops the token — fail closed again.
///  - The client UI is where the human consents to each command. The daemon
///    does NOT re-consent: it enforces the deny-list (CheckPolicy == Deny) and
///    the sudo policy as its last line of defense. Escalations need
///    <c>allow_sudo=allow</c> (no human at the daemon to answer an "ask").
///  - Every pair/exec/revoke/deny is written to the shared audit trail.
///
/// It reuses the exact execution engine the operator bridge uses
/// (<see cref="HostExecutor"/> + <see cref="HostIdentity"/>), so host policy,
/// sudo, run-as, timeouts, and the paper trail never diverge between surfaces.
/// </summary>
public sealed class HostDaemon : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly BridgeConfig _config;
    private readonly HostIdentity _identity;
    private readonly HostExecutor _executor;
    private readonly string _workDir;
    private readonly string _pairSecret;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, SessionLock> _sessions = [];
    private readonly object _sessionsLock = new();
    private readonly Dictionary<string, DateTime> _invites = [];
    private readonly object _invitesLock = new();
    private readonly Dictionary<string, PairRequest> _pairRequests = [];
    private readonly Dictionary<string, TaskCompletionSource<string?>> _pairWaiters = [];
    private static readonly TimeSpan DefaultPairRequestWait = TimeSpan.FromMinutes(2);
    private readonly TimeSpan _pairRequestWait;
    private readonly HashSet<string> _everPaired = new(StringComparer.Ordinal);
    private readonly TimeSpan _idleTimeout;
    private readonly string[] _extraBindAddresses;

    /// <summary>Addresses the daemon is actually listening on (loopback first).</summary>
    public string[] BoundAddresses { get; private set; } = ["127.0.0.1"];

    /// <summary>Default idle window after which a session token expires.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromHours(2);

    private sealed record SessionLock(string Token, string WorkDir, DateTime LastSeenUtc, DateTime PairedUtc);

    public int Port { get; }

    /// <param name="idleTimeout">Idle-token expiry: a pairing whose last seen
    /// activity is older than this dies on the next call (activity refreshes
    /// the clock). <see cref="TimeSpan.Zero"> disables expiry.</see></param>
    /// <param name="extraBindAddresses">Addresses beyond loopback to bind —
    /// docker bridge gateway IPs detected automatically, or explicit
    /// operator-supplied ones. Never 0.0.0.0/"+"; wildcards are rejected.</param>
    public HostDaemon(BridgeConfig config, HostIdentity identity, string workDir, int port,
        TimeSpan? idleTimeout = null, IEnumerable<string>? extraBindAddresses = null,
        TimeSpan? pairRequestWait = null)
    {
        _config = config;
        _identity = identity;
        _executor = new HostExecutor(config.HostExec, workDir, config.ResolvedLogDir, identity);
        _workDir = workDir;
        Port = port;
        _pairSecret = config.PairSecret;
        _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
        _pairRequestWait = pairRequestWait ??
            (config.PairRequestWaitSeconds > 0
                ? TimeSpan.FromSeconds(config.PairRequestWaitSeconds)
                : DefaultPairRequestWait);
        _extraBindAddresses = (extraBindAddresses ?? [])
            .Where(a => a is { Length: > 0 })
            .Where(a => a is not ("+" or "*") && a != "0.0.0.0")
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// The pairing secret the daemon requires. Empty secret here would let any
    /// process pair, so the caller must have ensured one exists (env or
    /// generated+saved to config) before starting the daemon.
    /// </summary>
    public string PairSecret => _pairSecret;

    public void Start()
    {
        var bound = new List<string> { "127.0.0.1" };
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        foreach (var addr in _extraBindAddresses)
        {
            // Containers reach the host at the bridge gateway; HttpListener
            // also matches the Host: header against bound prefixes, so the
            // client must dial the same address it is bound to (see
            // HostGatewayBootstrap, which resolves host.docker.internal).
            _listener.Prefixes.Add($"http://{addr}:{Port}/");
            bound.Add(addr);
        }
        BoundAddresses = [.. bound];
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>
    /// Bridge gateway IPs on this box that a sandbox container reaches as
    /// <c>host.docker.internal</c>. Linux: the docker bridge networks
    /// (docker0 / br-*). macOS: the vmnet shared bridges (bridge1xx — Lima /
    /// Colima's 192.168.5.x gateway and Docker Desktop's equivalent), so a
    /// Colima sandbox can dial the VM gateway and land on a bound prefix
    /// (Docker Desktop still routes through vpnkit to loopback, but binding
    /// bridge100 is harmless and covers both runtimes). bridge0 is excluded —
    /// it is Thunderbolt/AirDrop networking, not a container bridge. Empty
    /// when no such bridge exists. Loopback-only listeners are NOT reachable
    /// this way — that is why the daemon binds these too. Only ever local
    /// bridge subnets, never a LAN address. Down bridges are included: an
    /// idle docker0 (carrier=0) still owns its IP and still accepts
    /// connections routed in from other bridge networks — which is exactly
    /// how host-gateway resolves on multi-network hosts.
    /// </summary>
    public static string[] DetectDockerBridgeAddresses()
    {
        try
        {
            var vmBridgePrefix = OperatingSystem.IsMacOS() ? "bridge1" : null;
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback &&
                             (ni.Name.StartsWith("docker", StringComparison.Ordinal) ||
                              ni.Name.StartsWith("br-", StringComparison.Ordinal) ||
                              (vmBridgePrefix is not null &&
                               ni.Name.StartsWith(vmBridgePrefix, StringComparison.Ordinal))))
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                .Where(ua => ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(ua => ua.Address.ToString())
                .Distinct()
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception) when (ct.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }

            _ = HandleSafeAsync(ctx);
        }
    }

    private async Task HandleSafeAsync(HttpListenerContext ctx)
    {
        try
        {
            await HandleAsync(ctx);
        }
        catch (Exception ex)
        {
            AuditLog.WriteNote($"daemon request failed: {ex.Message}");
            try { WriteJson(ctx, 500, new StatusResponse(false, "internal error")); }
            catch { /* client gone */ }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "";
        var method = ctx.Request.HttpMethod;

        if (method == "GET" && path == "/ping")
        {
            WriteJson(ctx, 200, new PingResponse(true, true, BridgeOptions.Version));
            return;
        }

        if (method == "GET" && path == "/status")
        {
            // Counts only — never leaks tokens or session ids.
            int paired;
            lock (_sessionsLock)
                paired = _sessions.Count;
            // idle_timeout_seconds lets a paired client size its keepalive
            // heartbeat against this daemon's actual idle window (-1 = no expiry).
            int idleSeconds = _idleTimeout > TimeSpan.Zero ? (int)_idleTimeout.TotalSeconds : -1;
            WriteJson(ctx, 200, new DaemonStatusResponse(true, true, Port, BridgeOptions.Version, paired, idleSeconds));
            return;
        }

        if (method == "POST" && path == "/pair")
        {
            var req = await ReadJsonAsync<PairRequest>(ctx);
            await HandlePairAsync(ctx, req);
            return;
        }

        if (method == "POST" && path == "/exec")
        {
            var req = await ReadJsonAsync<ExecRequest>(ctx);
            await HandleExecAsync(ctx, req);
            return;
        }

        if (method == "POST" && path == "/revoke")
        {
            var req = await ReadJsonAsync<RevokeRequest>(ctx);
            await HandleRevokeAsync(ctx, req);
            return;
        }

        if (method == "POST" && path == "/verify")
        {
            var req = await ReadJsonAsync<RevokeRequest>(ctx);
            await HandleVerifyAsync(ctx, req);
            return;
        }

        if (method == "POST" && path == "/keepalive")
        {
            var req = await ReadJsonAsync<RevokeRequest>(ctx);
            HandleKeepalive(ctx, req);
            return;
        }

        if (path.StartsWith("/admin/", StringComparison.Ordinal))
        {
            await HandleAdminAsync(ctx, method, path);
            return;
        }

        WriteJson(ctx, 404, new StatusResponse(false, "not found"));
    }

    /// <summary>Raised when a pair request starts waiting for the operator
    /// (foreground daemon: the console approver listens and prompts [y/N]).</summary>
    public event Action<PairRequest>? PairRequested;

    /// <summary>Approve (or deny) a pending pair request by session id.
    /// Returns false when no request is pending for that id.</summary>
    public bool ResolvePairRequest(string sessionId, bool approve)
    {
        TaskCompletionSource<string?>? tcs;
        lock (_sessionsLock)
        {
            _pairWaiters.TryGetValue(sessionId, out tcs);
            _pairRequests.Remove(sessionId);
            _pairWaiters.Remove(sessionId);
        }
        if (tcs is null) return false;
        tcs.TrySetResult(approve ? "approved on host" : null);
        AuditLog.WriteNote($"pair request {(approve ? "approved" : "denied")} session={LogRedactor.Redact(sessionId)}");
        return true;
    }

    public bool RevokeSession(string sessionId)
    {
        bool removed;
        lock (_sessionsLock)
        {
            removed = _sessions.Remove(sessionId);
            // Operator-initiated kill: also drop the auto-approve memory so a
            // revoked session cannot silently re-pair itself — it must re-enter
            // the approval lane. (Idle expiry leaves the memory: a live agent
            // self-heals; an operator's decision does not get undone.) The
            // credential lane is untouched: an agent still holding a live
            // secret/invite can always re-pair with it — the credential, not
            // the revoke, is the security boundary.
            _everPaired.Remove(sessionId);
        }
        AuditLog.WriteNote($"admin revoke session={LogRedactor.Redact(sessionId)} removed={removed}");
        return removed;
    }

    public List<(string SessionId, string WorkDir)> ListPendingRequests()
    {
        lock (_sessionsLock)
            return _pairRequests.Values.Select(r => (r.SessionId, r.WorkDir)).ToList();
    }

    public List<(string SessionId, string WorkDir, string TokenHint, DateTime PairedUtc)> ListSessions()
    {
        lock (_sessionsLock)
            return _sessions.Select(kv => (kv.Key, kv.Value.WorkDir, kv.Value.Token[..6], kv.Value.PairedUtc)).ToList();
    }

    private async Task HandleAdminAsync(HttpListenerContext ctx, string method, string path)
    {
        // Admin surface = the pair secret (host-side CLI only; the sandbox
        // never sees the secret once compose stops mounting it). Everything
        // here is loopback/bridge-bound like the rest of the daemon.
        if (string.IsNullOrEmpty(_pairSecret) || ctx.Request.Headers["X-Pair-Secret"] != _pairSecret)
        {
            AuditLog.WriteNote($"admin refused (bad/missing pair secret) path={path}");
            WriteJson(ctx, 403, new StatusResponse(false, "bad_secret"));
            return;
        }

        if (method == "POST" && path == "/admin/invite")
        {
            // Optional body {"ttl_seconds":N}: the launcher mints long-lived
            // invites for manual /host on sessions; the boot-pair path keeps
            // the short config default. Clamped exactly like the config value.
            TimeSpan? ttl = null;
            if (ctx.Request.HasEntityBody)
            {
                try
                {
                    using var doc = await JsonDocument.ParseAsync(ctx.Request.InputStream);
                    // TryGetInt32 THROWS on non-number kinds (null included),
                    // so gate on ValueKind first.
                    if (doc.RootElement.TryGetProperty("ttl_seconds", out var t)
                        && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var secs))
                        ttl = TimeSpan.FromSeconds(Math.Clamp(secs, 30, 86_400));
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                { /* body optional: fall back to config TTL */ }
            }
            var invite = CreateInvite(ttl);
            WriteJson(ctx, 200, new InviteResponse(true, invite));
            return;
        }

        if (method == "GET" && path == "/admin/requests")
        {
            List<PairedSessionInfo> reqs;
            lock (_sessionsLock)
                reqs = _pairRequests.Values
                    .Select(r => new PairedSessionInfo(r.SessionId, r.WorkDir, "", "pending"))
                    .ToList();
            WriteJson(ctx, 200, new AdminListResponse(true, reqs));
            return;
        }

        if (method == "GET" && path == "/admin/sessions")
        {
            List<PairedSessionInfo> list;
            lock (_sessionsLock)
                list = _sessions.Select(kv => new PairedSessionInfo(
                        kv.Key, kv.Value.WorkDir, kv.Value.Token[..6],
                        kv.Value.PairedUtc.ToString("u")))
                    .ToList();
            WriteJson(ctx, 200, new AdminListResponse(true, list));
            return;
        }

        if (method == "POST" && (path == "/admin/approve" || path == "/admin/deny"))
        {
            var req = await ReadJsonAsync<ApproveRequest>(ctx);
            if (req is null || string.IsNullOrWhiteSpace(req.SessionId))
            {
                WriteJson(ctx, 400, new StatusResponse(false, "missing_session_id"));
                return;
            }
            TaskCompletionSource<string?>? tcs;
            lock (_sessionsLock)
            {
                _pairWaiters.TryGetValue(req.SessionId, out tcs);
                _pairRequests.Remove(req.SessionId);
                _pairWaiters.Remove(req.SessionId);
            }
            if (tcs is null)
            {
                WriteJson(ctx, 404, new StatusResponse(false, "no_pending_request"));
                return;
            }
            var approve = path == "/admin/approve";
            tcs.TrySetResult(approve ? "approved on host" : null);
            AuditLog.WriteNote($"pair request {(approve ? "approved" : "denied")} session={LogRedactor.Redact(req.SessionId)}");
            WriteJson(ctx, 200, new StatusResponse(true));
            return;
        }

        if (method == "POST" && path == "/admin/revoke")
        {
            // Host-side revoke: kills the session no matter what the agent
            // thinks; the agent's next exec fails closed with a clear reason.
            var req = await ReadJsonAsync<RevokeRequest>(ctx);
            if (req is null || string.IsNullOrWhiteSpace(req.SessionId))
            {
                WriteJson(ctx, 400, new StatusResponse(false, "missing_session_id"));
                return;
            }
            var removed = RevokeSession(req.SessionId);
            WriteJson(ctx, 200, new StatusResponse(removed, removed ? null : "no_such_session"));
            return;
        }

        WriteJson(ctx, 404, new StatusResponse(false, "not found"));
    }

    private async Task HandlePairAsync(HttpListenerContext ctx, PairRequest? req)
    {
        if (req is null)
        {
            WriteJson(ctx, 400, new PairResponse(false, Error: "invalid_json"));
            return;
        }

        if (string.IsNullOrWhiteSpace(req.SessionId))
        {
            WriteJson(ctx, 400, new PairResponse(false, Error: "missing_session_id"));
            return;
        }

        // Path 1: permanent pair secret (host-side admin + launcher back-compat).
        // auto_only (the gateway's stale-token self-heal) never uses a
        // credential and never queues an operator prompt: the ONLY way it can
        // pair is the pair_auto lane below, or it fails closed immediately. An
        // operator revoke or a hard-cap expiry clears the auto-approve memory,
        // so neither can a silent repair undo them — that is the whole point of
        // the cap and of revoke. Idle expiry keeps the memory on purpose: a
        // live agent self-heals; a long-dead one re-pairs through the normal
        // credential/approval lanes on its next explicit /host on.
        var autoOnly = req.AutoOnly;
        var viaSecret = !autoOnly && !string.IsNullOrEmpty(_pairSecret) && req.Secret == _pairSecret;

        // Path 2: launcher invite (TTL-bounded; valid for every pair from the
        // run that received it, including re-pairs). Preferred over the secret:
        // leaking it grants pairings only for the life of the invite, then it
        // is dead.
        var viaInvite = !autoOnly && !viaSecret && ConsumeInvite(req.Invite);

        // A wrong non-empty secret is an explicit refusal — never fall through
        // to the approval lane (an attacker guessing secrets must not be able
        // to queue operator prompts).
        if (!viaSecret && !viaInvite && !string.IsNullOrWhiteSpace(req.Secret))
        {
            AuditLog.WriteNote($"pair refused (bad secret) session={LogRedactor.Redact(req.SessionId)}");
            WriteJson(ctx, 403, new PairResponse(false, Error: "bad_secret"));
            return;
        }

        // Path 3: approval request. The agent waits; the operator approves on
        // the host (openmono host approve) — unless the auto-approve lane
        // applies: this daemon run already paired this exact session id, and
        // pair_auto allows it (default: this_session).
        string? viaRequest = null;
        if (!viaSecret && !viaInvite)
        {
            bool auto;
            lock (_sessionsLock)
            {
                auto = _config.PairAuto.Equals("this_session", StringComparison.OrdinalIgnoreCase) &&
                       _everPaired.Contains(req.SessionId);
                // A silent self-heal (auto_only) must never steal a LIVE
                // pairing: a session id that is currently paired under a
                // different token was taken over by another gateway on
                // purpose — fighting it back would ping-pong tokens.
                if (autoOnly && auto && _sessions.ContainsKey(req.SessionId))
                    auto = false;
            }
            if (auto)
            {
                viaRequest = "auto-approved (pair_auto=this_session, seen before)";
                AuditLog.WriteNote($"pair auto-approved session={LogRedactor.Redact(req.SessionId)}");
            }
            else if (autoOnly)
            {
                // Self-heal against a session this daemon will not auto-approve
                // (never paired here, operator-revoked, cap-expired, or
                // pair_auto=strict): fail fast, never queue a prompt.
                AuditLog.WriteNote($"pair auto-repair refused session={LogRedactor.Redact(req.SessionId)}");
                WriteJson(ctx, 403, new PairResponse(false, Error: "pair_auto_unavailable"));
                return;
            }
            else
            {
                var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (_sessionsLock)
                {
                    _pairRequests[req.SessionId] = req;
                    _pairWaiters[req.SessionId] = tcs;
                }
                AuditLog.WriteNote($"pair request pending session={LogRedactor.Redact(req.SessionId)} workdir={req.WorkDir}");
                PairRequested?.Invoke(req);
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(_pairRequestWait));
                lock (_sessionsLock)
                {
                    _pairRequests.Remove(req.SessionId);
                    _pairWaiters.Remove(req.SessionId);
                }
                if (completed == tcs.Task)
                    viaRequest = await tcs.Task;
                else
                {
                    AuditLog.WriteNote($"pair request timed out session={LogRedactor.Redact(req.SessionId)}");
                    WriteJson(ctx, 403, new PairResponse(false, Error: "pair_timeout"));
                    return;
                }
                if (viaRequest is null)
                {
                    WriteJson(ctx, 403, new PairResponse(false, Error: "pair_denied"));
                    return;
                }
            }
        }

        var token = NewToken();
        lock (_sessionsLock)
        {
            // Re-pair rotates the token: a stale token can never keep working.
            _sessions[req.SessionId] = new SessionLock(token, string.IsNullOrWhiteSpace(req.WorkDir) ? _workDir : req.WorkDir, DateTime.UtcNow, DateTime.UtcNow);
            _everPaired.Add(req.SessionId);
        }

        AuditLog.WriteNote($"pair ok session={LogRedactor.Redact(req.SessionId)} workdir={req.WorkDir} run_as={_identity.RunAsUser} sudo={_identity.Sudo.ToString().ToLowerInvariant()} via={(viaSecret ? "secret" : viaInvite ? "invite" : "request")}");
        WriteJson(ctx, 200, new PairResponse(true, token, _identity.RunAsUser, _identity.Sudo.ToString().ToLowerInvariant(), _config.ResolvedLogDir, Reason: viaRequest));
    }

    private async Task HandleExecAsync(HttpListenerContext ctx, ExecRequest? req)
    {
        if (req is null || req.Request is null)
        {
            WriteJson(ctx, 400, new ExecResponse(false, Denied: true, Error: "invalid_json"));
            return;
        }

        // Session lock: token + session id must match a live pairing. Mismatch
        // fails closed — no token, expired token, wrong session = refuse.
        if (!ValidateToken(req.SessionId, req.Token, out var workDir))
        {
            AuditLog.WriteCommand(_identity.RunAsUser, req.Request.AsRoot, req.Request.Command,
                -1, 0, false, "exec refused: no live session token (fail closed)");
            WriteJson(ctx, 403, new ExecResponse(false, Denied: true, Error: "unpaired_or_bad_token"));
            return;
        }

        // Last-line-of-defense policy: the client UI already obtained the user's
        // consent, so here we only enforce the deny-list. CheckPolicy returns
        // Deny only when a deny pattern (or default deny) matches; Ask/Allow are
        // both "the user said yes, run it".
        var decision = _executor.CheckPolicy(req.Request.Command);
        if (decision == HostPolicyDecision.Deny)
        {
            AuditLog.WriteCommand(_identity.RunAsUser, req.Request.AsRoot, req.Request.Command,
                -1, 0, false, "denied by daemon deny-list policy");
            WriteJson(ctx, 200, new ExecResponse(false,
                Output: $"Host execution denied by host-bridge policy (deny-list): {req.Request.Command}",
                Denied: true));
            return;
        }

        // Sudo: there is no human at the daemon to answer an "ask", so an
        // escalation needs either an explicit allow_sudo=allow or a per-exec
        // password carried by THIS request from the client consent card (the
        // operator consented to exactly this command and typed a password
        // there). Neither → fail closed.
        var elevation = _executor.RequiresElevation(req.Request);
        var perExecPassword = elevation && !_identity.HasPassword
            && !string.IsNullOrWhiteSpace(req.Password);
        if (elevation && _identity.Sudo != SudoPolicy.Allow && !perExecPassword)
        {
            AuditLog.WriteCommand(_identity.RunAsUser, req.Request.AsRoot, req.Request.Command,
                -1, 0, false, "sudo refused by daemon (no human at daemon; allow_sudo not allow and no per-exec password)");
            WriteJson(ctx, 200, new ExecResponse(false,
                Output: "Host execution denied by host-bridge policy (sudo needs a per-exec password from the client consent card, or allow_sudo=allow for unattended host mode).",
                Denied: true));
            return;
        }

        if (perExecPassword)
            AuditLog.WriteNote("exec used a per-exec sudo password (value never logged)");

        // No prompter: a command that blocks on a stdin secret cannot be served
        // unattended; the executor's prompt loop fails fast with guidance.
        // The per-exec password rides the identity for this exec ONLY and is
        // wiped on every return path — never cached, logged, or persisted.
        if (perExecPassword)
            _identity.SetPassword(req.Password!.ToCharArray());
        string output;
        try
        {
            output = await _executor.ExecuteAsync(req.Request, _config.TimeoutMs, _cts.Token, prompter: null);
        }
        finally
        {
            if (perExecPassword)
                _identity.ForgetPassword();
        }
        var denied = output.StartsWith("ERROR:", StringComparison.Ordinal);
        if (perExecPassword && HostExecutor.IsPasswordFailure(output))
        {
            // Wrong password: the executor already wiped the cached timestamp
            // probe; tell the client it may re-prompt (long-lived consent
            // modal) instead of surfacing this as a policy denial. The
            // operator's in-memory secret (password file) is untouched.
            HostExecutor.NoteAuthenticationFailure(req.Request.AsRoot ? "root" : _identity.RunAsUser);
            AuditLog.WriteCommand(_identity.RunAsUser, req.Request.AsRoot, req.Request.Command,
                -1, 0, false, "sudo authentication failed; per-exec password wiped, client may retry");
            WriteJson(ctx, 200, new ExecResponse(false,
                Output: "sudo authentication failed — the password was wiped; the user may retry.",
                Denied: true, Error: "password_failure"));
            return;
        }
        WriteJson(ctx, 200, new ExecResponse(!denied, output, denied));
    }

    private async Task HandleRevokeAsync(HttpListenerContext ctx, RevokeRequest? req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.SessionId))
        {
            WriteJson(ctx, 400, new StatusResponse(false, "invalid_json"));
            return;
        }

        bool removed;
        lock (_sessionsLock)
        {
            // Revoke must match the token (a stranger cannot revoke someone
            // else's session), then drops it.
            if (_sessions.TryGetValue(req.SessionId, out var s) && s.Token == req.Token)
            {
                _sessions.Remove(req.SessionId);
                removed = true;
            }
            else
            {
                removed = false;
            }
        }

        AuditLog.WriteNote($"revoke session={LogRedactor.Redact(req.SessionId)} removed={removed}");
        WriteJson(ctx, 200, new StatusResponse(removed, removed ? null : "no_such_session_or_token"));
    }

    /// <summary>
    /// Liveness check for one session's own token (POST /verify). Unlike the
    /// global /status paired-count, this validates the exact pairing the caller
    /// holds: a dead or rotated token answers live=false even while other
    /// sessions remain paired. A successful verify counts as activity and
    /// refreshes the idle clock, like exec does.
    /// </summary>
    private async Task HandleVerifyAsync(HttpListenerContext ctx, RevokeRequest? req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.SessionId) || string.IsNullOrWhiteSpace(req.Token))
        {
            WriteJson(ctx, 200, new VerifyResponse(true, Daemon: true, Live: false));
            return;
        }
        var live = VerifyToken(req.SessionId, req.Token);
        WriteJson(ctx, 200, new VerifyResponse(true, Daemon: true, Live: live));
    }

    /// <summary>Same session+token match as exec (so liveness never lies when
    /// exec would still fail), but WITHOUT the idle/hard-cap expiry side
    /// effects: expiry must stay exec's decision, fail closed.</summary>
    private bool VerifyToken(string sessionId, string token)
    {
        lock (_sessionsLock)
            return _sessions.TryGetValue(sessionId, out var s) && s.Token == token;
    }

    /// <summary>
    /// Liveness check for one session's own token (POST /keepalive). Unlike
    /// /verify, a successful keepalive REFRESHES the idle clock — the paired
    /// agent's heartbeat keeps its pairing alive while its process lives, so
    /// idle expiry becomes an orphan-GC for dead agents instead of an
    /// ambush on live ones. The hard cap (<c>token_max_hours</c>) is still
    /// enforced here: a heartbeat must never extend a pairing past the cap,
    /// and an over-cap session is dropped (fail closed, same as exec).
    /// </summary>
    private void HandleKeepalive(HttpListenerContext ctx, RevokeRequest? req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.SessionId) || string.IsNullOrWhiteSpace(req.Token))
        {
            WriteJson(ctx, 200, new StatusResponse(false, "missing_session_id_or_token"));
            return;
        }
        var result = RefreshSessionIdle(req.SessionId, req.Token);
        WriteJson(ctx, 200, result switch
        {
            "ok" => new StatusResponse(true),
            "expired" => new StatusResponse(false, "token_expired"),
            _ => new StatusResponse(false, "no_such_session_or_token"),
        });
    }

    /// <summary>ok | stale (no such session/token) | expired (over hard cap —
    /// session dropped). Idle expiry is checked before it can bite: a
    /// heartbeat that arrives while the token is still live refreshes it.</summary>
    private string RefreshSessionIdle(string sessionId, string token)
    {
        lock (_sessionsLock)
        {
            if (!_sessions.TryGetValue(sessionId, out var s) || s.Token != token)
                return "stale";
            var maxHours = _config.TokenMaxHours;
            if (maxHours > 0 &&
                DateTime.UtcNow - s.PairedUtc > TimeSpan.FromHours(maxHours))
            {
                _sessions.Remove(sessionId);
                _everPaired.Remove(sessionId);
                AuditLog.WriteNote($"session token expired (max {maxHours}h, keepalive) session={LogRedactor.Redact(sessionId)}");
                return "expired";
            }
            _sessions[sessionId] = s with { LastSeenUtc = DateTime.UtcNow };
            return "ok";
        }
    }

    private bool ValidateToken(string? sessionId, string? token, out string workDir)
    {
        workDir = _workDir;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(token))
            return false;
        lock (_sessionsLock)
        {
            if (!_sessions.TryGetValue(sessionId, out var s) || s.Token != token)
                return false;
            // Idle-token expiry: an abandoned pairing dies on its next call
            // (fail closed); any validated call refreshes the clock, so an
            // active session never expires out from under its agent.
            if (_idleTimeout > TimeSpan.Zero &&
                DateTime.UtcNow - s.LastSeenUtc > _idleTimeout)
            {
                _sessions.Remove(sessionId);
                AuditLog.WriteNote($"session token expired after idle session={LogRedactor.Redact(sessionId)}");
                return false;
            }
            // Hard cap since pairing (0 = no cap): forces the agent back
            // through the approval lane even if it never goes idle.
            var maxHours = _config.TokenMaxHours;
            if (maxHours > 0 &&
                DateTime.UtcNow - s.PairedUtc > TimeSpan.FromHours(maxHours))
            {
                _sessions.Remove(sessionId);
                // The cap's whole point is forcing the approval lane — clear
                // the auto-approve memory too, or an agent-side silent
                // re-pair would defeat it (the credential lane stays open
                // by design: a live secret is a live pairing right).
                _everPaired.Remove(sessionId);
                AuditLog.WriteNote($"session token expired (max {maxHours}h) session={LogRedactor.Redact(sessionId)}");
                return false;
            }
            _sessions[sessionId] = s with { LastSeenUtc = DateTime.UtcNow };
            workDir = s.WorkDir;
            return true;
        }
    }

    private static string NewToken()
    {
        var bytes = new byte[24];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes);
    }

    /// <summary>Mint a pairing invite (launcher path). The invite is
    /// TTL-bounded: it stays valid for every pair from the run that received
    /// it (re-pairs after a daemon restart included), and dies at its TTL.
    /// It never grants pairing past the TTL, whatever its use count.</summary>
    public string CreateInvite(TimeSpan? ttlOverride = null)
    {
        var invite = NewToken();
        var ttl = ttlOverride ?? TimeSpan.FromSeconds(Math.Clamp(_config.InviteTtlSeconds, 30, 86_400));
        ttl = TimeSpan.FromSeconds(Math.Clamp((int)ttl.TotalSeconds, 30, 86_400));
        lock (_invitesLock)
        {
            PruneInvites();
            _invites[invite] = DateTime.UtcNow.Add(ttl);
        }
        AuditLog.WriteNote($"invite issued ttl_s={(int)ttl.TotalSeconds}");
        return invite;
    }

    private bool ConsumeInvite(string? invite)
    {
        if (string.IsNullOrWhiteSpace(invite)) return false;
        lock (_invitesLock)
        {
            // TTL-bounded, not single-use: a launcher invite is handed to one
            // container run, and every session in that run may pair with it
            // until it expires. Consuming it after first use made every later
            // session in the same container fall into the approval lane with
            // no credential — pair_timeout, host mode stuck "off".
            if (!_invites.TryGetValue(invite, out var expires)) return false;
            return expires >= DateTime.UtcNow;
        }
    }

    private void PruneInvites()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _invites.Where(kv => kv.Value < now).ToList())
            _invites.Remove(kv.Key);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream);
        var raw = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(raw))
            return default;
        try
        {
            return JsonSerializer.Deserialize<T>(raw, BridgeConfig.WebOptions());
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static void WriteJson<T>(HttpListenerContext ctx, int status, T body)
    {
        var json = JsonSerializer.Serialize(body, BridgeConfig.WebOptions());
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        lock (_sessionsLock)
            _sessions.Clear();
    }
}
