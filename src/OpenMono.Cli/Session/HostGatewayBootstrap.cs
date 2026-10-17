namespace OpenMono.Session;

/// <summary>
/// Creates and pairs <see cref="HostGateway"/> instances. When the launcher
/// runs <c>openmono agent --host</c> it starts a host-bridge daemon on the
/// host machine and injects the pairing details into the sandbox, so host
/// mode is live from the first turn. Without that env, <c>/host on</c> can
/// still pair manually (same machine: daemon + agent side by side).
///
/// Env contract (launcher → sandbox):
///  - OPENMONO_HOST_MODE=1       host mode intended for this run
///  - OPENMONO_HOST_DAEMON_URL   e.g. http://host.docker.internal:7476
///  - OPENMONO_HOST_PAIR_SECRET  shared secret (host ~/.openmono/host-bridge.json)
///  - OPENMONO_HOST_INVITE       pairing invite (TTL-bounded; every session
///                               in the run may pair with it until it expires);
///                               preferred over the secret when both are set
///  - OPENMONO_HOST_WORKSPACE    host directory bind-mounted at /workspace
/// </summary>
public static class HostGatewayBootstrap
{
    public const string ModeEnv = "OPENMONO_HOST_MODE";
    public const string UrlEnv = "OPENMONO_HOST_DAEMON_URL";
    public const string SecretEnv = "OPENMONO_HOST_PAIR_SECRET";
    public const string InviteEnv = "OPENMONO_HOST_INVITE";
    public const string WorkDirEnv = "OPENMONO_HOST_WORKSPACE";
    public const string HostOsEnv = "OPENMONO_HOST_OS";
    public const string DefaultPort = "7476";

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>True when the launcher marked this run as host mode.</summary>
    public static bool EnvEnabled =>
        Environment.GetEnvironmentVariable(ModeEnv) is { Length: > 0 } &&
        Environment.GetEnvironmentVariable(ModeEnv) != "0";

    public static string ResolveBaseUrl()
    {
        var url = Environment.GetEnvironmentVariable(UrlEnv);
        Uri? parsed = null;
        if (!string.IsNullOrWhiteSpace(url))
            Uri.TryCreate(url.Trim(), UriKind.Absolute, out parsed);
        var host = parsed?.Host is { Length: > 0 } h ? h : (File.Exists("/.dockerenv") ? "host.docker.internal" : "127.0.0.1");
        var port = parsed is not null && parsed.Port > 0 ? parsed.Port : int.Parse(DefaultPort);

        // Keep the literal host.docker.internal on every runtime. Managed
        // HttpListener dispatches by the Host: header, not by interface, and
        // HostDaemonClient rewrites that header to 127.0.0.1:<port> — a bound
        // prefix wherever the daemon runs. DNS handles the routing: the name
        // resolves to host-gateway on Linux docker, vpnkit on Docker Desktop,
        // vmnet on Colima/Lima. An earlier attempt to dial the resolved IPv4
        // (gated on the host's OS) regressed this: when OPENMONO_HOST_OS was
        // unset, the Linux best-guess fired on macOS too, dialing the vpnkit
        // gateway with no header override — a 404 that reads as "daemon not
        // reachable". Any explicit OPENMONO_HOST_DAEMON_URL stays verbatim.
        return $"http://{host}:{port}";
    }

    public static string ResolveSecret()
    {
        var env = Environment.GetEnvironmentVariable(SecretEnv);
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        // Same-machine manual pairing: the daemon persists its secret here.
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".openmono", "host-bridge.json");
        try
        {
            if (File.Exists(path))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("pair_secret", out var s))
                    return s.GetString() ?? "";
            }
        }
        catch { /* unreadable config = no secret */ }
        return "";
    }

    public static string ResolveInvite() =>
        (Environment.GetEnvironmentVariable(InviteEnv) ?? "").Trim();

    /// <summary>
    /// True when the launcher handed this sandbox a pairing credential (invite
    /// or secret). Without one, pairing enters request mode: the daemon parks
    /// the request until an operator runs `openmono host approve`.
    /// </summary>
    public static bool HasCredential() =>
        !string.IsNullOrWhiteSpace(ResolveSecret()) || !string.IsNullOrWhiteSpace(ResolveInvite());

    public static string ResolveWorkDir()
    {
        var wd = Environment.GetEnvironmentVariable(WorkDirEnv);
        return string.IsNullOrWhiteSpace(wd) ? "/workspace" : wd.Trim();
    }

    /// <summary>
    /// The live gateway for this session, pairing when the launcher env says
    /// host mode is intended. Returns null (never throws) when host mode is
    /// not configured for this run.
    /// </summary>
    public static Task<HostGateway?> GetOrCreateAsync(SessionState session, CancellationToken ct = default) =>
        CreateCoreAsync(session, force: false, ct);

    /// <summary>
    /// Manual pairing (<c>/host on</c>): pairs regardless of env as long as a
    /// secret can be resolved. Returns the gateway or a human-readable reason.
    /// </summary>
    public static Task<(HostGateway? Gateway, string? Error)> TryPairAsync(SessionState session, CancellationToken ct = default) =>
        PairCoreAsync(session, ct);

    private static async Task<HostGateway?> CreateCoreAsync(SessionState session, bool force, CancellationToken ct)
    {
        var existing = HostGatewayRegistry.Get(session.Id);
        if (existing is not null)
            return existing;

        if (!force && !EnvEnabled)
            return null;

        var secret = ResolveSecret();
        var invite = ResolveInvite();
        // Auto-pair only when the launcher handed us a credential (invite or
        // secret). Without one we do not queue an approval prompt for a session
        // that never asked — manual /host on is the request-mode entry point.
        if (string.IsNullOrWhiteSpace(secret) && string.IsNullOrWhiteSpace(invite))
            return null;

        await Gate.WaitAsync(ct);
        try
        {
            existing = HostGatewayRegistry.Get(session.Id);
            if (existing is not null)
                return existing;

            var gateway = new HostGateway(ResolveBaseUrl(), session.Id, ResolveWorkDir(), secret, invite);
            var error = await gateway.PairAsync(ct: ct);
            if (error is not null)
            {
                Utils.Log.Info($"[HOSTGATEWAY] pair failed for session {session.Id}: {error}");
                return null;
            }

            HostGatewayRegistry.Register(session.Id, gateway);
            Utils.Log.Info($"[HOSTGATEWAY] paired session {session.Id} at {gateway.BaseUrl} run_as={gateway.RunAs} sudo={gateway.Sudo}");
            return gateway;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<(HostGateway? Gateway, string? Error)> PairCoreAsync(SessionState session, CancellationToken ct)
    {
        var existing = HostGatewayRegistry.Get(session.Id);
        if (existing is { IsPaired: true } && await existing.IsPairingLiveAsync(ct))
            return (existing, null);

        if (existing is not null)
        {
            // Unpaired or stale (daemon restarted — tokens are in-memory):
            // unregister so the fresh pairing below replaces the dead one.
            HostGatewayRegistry.Unregister(session.Id);
        }

        var secret = ResolveSecret();
        var invite = ResolveInvite();
        // Manual pairing may run with no credential at all: the daemon then
        // treats it as a pair *request* the operator approves on the host
        // (openmono host approve). This is the request-mode entry point.

        var gateway = new HostGateway(ResolveBaseUrl(), session.Id, ResolveWorkDir(), secret, invite);
        if (string.IsNullOrWhiteSpace(secret) && string.IsNullOrWhiteSpace(invite))
        {
            // Request mode: PairAsync now blocks up to the daemon's approval
            // timeout. Say so up front, with the exact approve command, so
            // the wait is never mistaken for a hang.
            Utils.Log.Info($"[HOSTGATEWAY] pair request (no credential) for session {session.Id} — " +
                $"operator must run: openmono host approve {session.Id}");
        }
        var error = await gateway.PairAsync(ct: ct);
        if (error is not null)
            return (null, error);

        HostGatewayRegistry.Register(session.Id, gateway);
        Utils.Log.Info($"[HOSTGATEWAY] paired (manual) session {session.Id} at {gateway.BaseUrl} run_as={gateway.RunAs} sudo={gateway.Sudo}");
        return (gateway, null);
    }
}
