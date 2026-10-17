using System.Net.Sockets;
using FluentAssertions;
using OpenMono.HostBridge;
using OpenMono.Session;

namespace OpenMono.Tests.HostBridge;

/// <summary>
/// End-to-end host mode: the real HostDaemon on a loopback port, driven by
/// the sandbox-side HostGateway (the Cli's /host on + HOST_EXEC relay). Verifies
/// pairing, consent flow, session grants, sudo refusal, deny-list enforcement,
/// and fail-closed behavior when the pairing dies.
/// </summary>
public class HostGatewayTests : IAsyncLifetime
{
    private static readonly string Secret = "test-pair-secret-3f9a2c";

    private HostDaemon _daemon = null!;
    private string _workDir = null!;

    public Task InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "openmono-hostgw-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_workDir);

        var config = new BridgeConfig
        {
            PairSecret = Secret,
            HostExec = new HostExecPolicy
            {
                Default = "allow",
                Deny = [.. HostExecPolicy.SampleDeny],
            },
        };
        var identity = new HostIdentity("", SudoPolicy.Ask, null);
        _daemon = new HostDaemon(config, identity, _workDir, GetFreePort());
        _daemon.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _daemon.DisposeAsync();
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best effort */ }
    }

    private HostGateway NewGateway(string sessionId = "gw-test") =>
        new($"http://127.0.0.1:{_daemon.Port}", sessionId, _workDir, Secret);

    // ── Pairing ────────────────────────────────────────────────────────

    [Fact]
    public async Task Pair_CorrectSecret_SucceedsAndCarriesIdentity()
    {
        var gw = NewGateway();
        var error = await gw.PairAsync();
        error.Should().BeNull();
        gw.IsPaired.Should().BeTrue();
        gw.RunAs.Should().NotBeNullOrWhiteSpace();
        gw.Sudo.Should().Be(SudoPolicy.Ask);
    }

    [Fact]
    public async Task Pair_WrongSecret_FailsClosed()
    {
        var gw = new HostGateway($"http://127.0.0.1:{_daemon.Port}", "gw-bad", _workDir, "wrong-secret");
        var error = await gw.PairAsync();
        error.Should().NotBeNull();
        gw.IsPaired.Should().BeFalse();
    }

    [Fact]
    public async Task Pair_UnreachableDaemon_ReportsReason()
    {
        var gw = new HostGateway("http://127.0.0.1:1", "gw-down", _workDir, Secret);
        var error = await gw.PairAsync();
        error.Should().NotBeNull().And.Contain("not reachable");
        gw.IsPaired.Should().BeFalse();
    }

    // ── Bridge-address binding (Linux host.docker.internal reachability) ──

    [Fact]
    public async Task Pair_ExtraBoundAddress_PairAndExecSucceedViaIt()
    {
        // 127.0.0.2 is a real loopback address on every Linux/macOS box; it
        // stands in for a docker bridge gateway IP the daemon now binds.
        // HttpListener matches the Host: header against bound prefixes, so a
        // client that dials the bound address must succeed end-to-end.
        await using var daemon = new HostDaemon(
            new BridgeConfig { PairSecret = Secret, HostExec = new HostExecPolicy { Default = "allow", Deny = [.. HostExecPolicy.SampleDeny] } },
            new HostIdentity("", SudoPolicy.Ask, null), _workDir, GetFreePort(),
            null, ["127.0.0.2"]);
        daemon.Start();

        var gw = new HostGateway($"http://127.0.0.2:{daemon.Port}", "gw-bridge", _workDir, Secret);
        var error = await gw.PairAsync();
        error.Should().BeNull();
        gw.IsPaired.Should().BeTrue();

        var result = await gw.RunAsync(
            new HostExecRequest("echo bridge-ok", null, false, false),
            (_, _, _) => Task.FromResult(HostConsentDecision.AllowOnce),
            CancellationToken.None);
        result.Should().Contain("bridge-ok");
    }

    [Fact]
    public async Task Pair_HostHeaderMismatch_FailsClosed()
    {
        // The other half of the Linux bug: dialing an address the daemon does
        // NOT bind gets rejected by HttpListener (404 on the wire). This is
        // why the agent must resolve host.docker.internal to the gateway IP
        // the daemon binds, not just hope the hostname matches.
        await using var daemon = new HostDaemon(
            new BridgeConfig { PairSecret = Secret, HostExec = new HostExecPolicy { Default = "allow", Deny = [.. HostExecPolicy.SampleDeny] } },
            new HostIdentity("", SudoPolicy.Ask, null), _workDir, GetFreePort());
        daemon.Start();

        var gw = new HostGateway($"http://127.0.0.2:{daemon.Port}", "gw-mismatch", _workDir, Secret);
        var error = await gw.PairAsync();
        error.Should().NotBeNull();
        gw.IsPaired.Should().BeFalse();
    }

    [Fact]
    public void DetectDockerBridgeAddresses_NeverWildcardAndNeverThrows()
    {
        var addrs = HostDaemon.DetectDockerBridgeAddresses();
        addrs.Should().NotBeNull();
        foreach (var a in addrs)
        {
            a.Should().NotBe("0.0.0.0").And.NotBe("+").And.NotBe("*");
            System.Net.IPAddress.TryParse(a, out var parsed).Should().BeTrue();
            parsed!.AddressFamily.Should().Be(AddressFamily.InterNetwork);
        }
    }

    [Fact]
    public async Task Start_RejectsWildcardBind()
    {
        await using var daemon = new HostDaemon(
            new BridgeConfig { PairSecret = Secret, HostExec = new HostExecPolicy { Default = "allow", Deny = [.. HostExecPolicy.SampleDeny] } },
            new HostIdentity("", SudoPolicy.Ask, null), _workDir, GetFreePort(),
            null, ["0.0.0.0", "+"]);
        daemon.Start();
        daemon.BoundAddresses.Should().BeEquivalentTo(["127.0.0.1"]);
        // Wildcard sneaked in? Nothing should answer on the bridge stand-in.
        var gw = new HostGateway($"http://127.0.0.2:{daemon.Port}", "gw-wild", _workDir, Secret);
        (await gw.PairAsync()).Should().NotBeNull();
    }

    // ── Execution + consent ───────────────────────────────────────────

    [Fact]
    public async Task Run_OnceApproved_ExecutesOnHostAndReturnsOutput()
    {
        var gw = NewGateway();
        (await gw.PairAsync()).Should().BeNull();

        var answer = await gw.RunAsync(
            new HostExecRequest("echo host-exec-ok", null, false, false),
            (_, _, _) => Task.FromResult(HostConsentDecision.AllowOnce),
            CancellationToken.None);

        answer.Should().Contain("host-exec-ok");
        answer.Should().MatchRegex("^\\[host-exec on .*\\]\\n\\$ echo host-exec-ok\\n");
    }

    [Fact]
    public async Task Run_DeniedByUser_NeverReachesDaemon()
    {
        var gw = NewGateway();
        (await gw.PairAsync()).Should().BeNull();

        var marker = Path.Combine(_workDir, "never-created-" + Guid.NewGuid().ToString("N"));
        var answer = await gw.RunAsync(
            new HostExecRequest($"touch {marker}", null, false, false),
            (_, _, _) => Task.FromResult(HostConsentDecision.DenyOnce),
            CancellationToken.None);

        answer.Should().Contain("denied by user");
        File.Exists(marker).Should().BeFalse();
    }

    [Fact]
    public async Task Run_AllowAll_SkipsLaterConsentPrompts()
    {
        var gw = NewGateway();
        (await gw.PairAsync()).Should().BeNull();

        var prompts = 0;
        var first = await gw.RunAsync(
            new HostExecRequest("echo one", null, false, false),
            (_, _, _) => { prompts++; return Task.FromResult(HostConsentDecision.AllowAll); },
            CancellationToken.None);
        first.Should().Contain("one");

        // allow-all covers plain commands: no second prompt.
        var second = await gw.RunAsync(
            new HostExecRequest("echo two", null, false, false),
            (_, _, _) => { prompts++; return Task.FromResult(HostConsentDecision.AllowOnce); },
            CancellationToken.None);
        second.Should().Contain("two");
        prompts.Should().Be(1);
        gw.SessionAllowsAll.Should().BeTrue();
    }

    [Fact]
    public async Task Run_AllowAll_DoesNotCoverEscalation()
    {
        var gw = NewGateway();
        (await gw.PairAsync()).Should().BeNull();

        await gw.RunAsync(
            new HostExecRequest("echo one", null, false, false),
            (_, _, _) => Task.FromResult(HostConsentDecision.AllowAll),
            CancellationToken.None);

        var prompts = 0;
        var answer = await gw.RunAsync(
            new HostExecRequest("true", null, false, true),
            (_, _, _) => { prompts++; return Task.FromResult(HostConsentDecision.AllowOnce); },
            CancellationToken.None);

        // as_root must always re-prompt, and the daemon's sudo gate (ask, no
        // human) still refuses the escalation itself.
        prompts.Should().Be(1);
        answer.Should().Contain("sudo");
    }

    [Fact]
    public async Task Run_AsRoot_WithSudoDeny_RefusedBeforeConsent()
    {
        using var denyIdentity = new HostIdentity("", SudoPolicy.Deny, null);
        await using var denyDaemon = new HostDaemon(
            new BridgeConfig { PairSecret = Secret, HostExec = new HostExecPolicy { Default = "allow" } },
            denyIdentity, _workDir, GetFreePort());
        denyDaemon.Start();

        var gw = new HostGateway($"http://127.0.0.1:{denyDaemon.Port}", "gw-sudo-deny", _workDir, Secret);
        (await gw.PairAsync()).Should().BeNull();
        gw.Sudo.Should().Be(SudoPolicy.Deny);

        var prompts = 0;
        var answer = await gw.RunAsync(
            new HostExecRequest("whoami", null, false, true),
            (_, _, _) => { prompts++; return Task.FromResult(HostConsentDecision.AllowOnce); },
            CancellationToken.None);

        prompts.Should().Be(0);
        answer.Should().Contain("sudo is denied");
    }

    [Fact]
    public async Task Run_DenyListBlocked_EvenWithConsent()
    {
        var gw = NewGateway();
        (await gw.PairAsync()).Should().BeNull();

        var answer = await gw.RunAsync(
            new HostExecRequest("rm -rf /", null, false, false),
            (_, _, _) => Task.FromResult(HostConsentDecision.AllowOnce),
            CancellationToken.None);

        answer.Should().Contain("denied by host-bridge policy");
    }

    [Fact]
    public async Task Run_DenyAll_StopsSessionHostAccess()
    {
        var gw = NewGateway();
        (await gw.PairAsync()).Should().BeNull();

        var first = await gw.RunAsync(
            new HostExecRequest("echo one", null, false, false),
            (_, _, _) => Task.FromResult(HostConsentDecision.DenyAll),
            CancellationToken.None);
        first.Should().Contain("denied by user");
        gw.SessionDeniesAll.Should().BeTrue();

        // A later command is refused without another prompt.
        var prompts = 0;
        var second = await gw.RunAsync(
            new HostExecRequest("echo two", null, false, false),
            (_, _, _) => { prompts++; return Task.FromResult(HostConsentDecision.AllowOnce); },
            CancellationToken.None);
        second.Should().Contain("denied by user");
        prompts.Should().Be(0);
    }

    // ── Unpair + fail-closed ───────────────────────────────────────────

    [Fact]
    public async Task Unpair_RevokeStopsExecution()
    {
        var gw = NewGateway();
        (await gw.PairAsync()).Should().BeNull();
        await gw.UnpairAsync();
        gw.IsPaired.Should().BeFalse();

        var answer = await gw.RunAsync(
            new HostExecRequest("echo should-not-run", null, false, false),
            (_, _, _) => Task.FromResult(HostConsentDecision.AllowOnce),
            CancellationToken.None);
        answer.Should().Contain("not paired");
    }

    [Fact]
    public async Task Run_WithStaleToken_FailsClosedAfterRotation()
    {
        var gw = NewGateway("shared-session");
        (await gw.PairAsync()).Should().BeNull();

        // A second gateway pairing the same session id rotates the token; the
        // first gateway's token is dead — its execs must fail closed.
        var other = NewGateway("shared-session");
        (await other.PairAsync()).Should().BeNull();

        var answer = await gw.RunAsync(
            new HostExecRequest("echo stale", null, false, false),
            (_, _, _) => Task.FromResult(HostConsentDecision.AllowOnce),
            CancellationToken.None);
        answer.Should().Contain("pairing is stale");
    }

    [Fact]
    public async Task IsPairingLive_DeadTokenReadsStaleEvenWithAnotherSessionPaired()
    {
        // The global paired-count liveness bug: session A is paired, session B
        // is paired then revoked. The old IsPairingLiveAsync asked the daemon
        // "any paired sessions?" (yes — A), so B's dead token read "live" and
        // /host on no-opped while every exec failed unpaired_or_bad_token. The
        // per-token /verify check must report B stale while A stays live.
        var a = NewGateway("live-a");
        var b = NewGateway("dead-b");
        (await a.PairAsync()).Should().BeNull();
        (await b.PairAsync()).Should().BeNull();

        // Kill B's daemon-side pairing, leaving A paired.
        _daemon.RevokeSession("dead-b");

        (await b.IsPairingLiveAsync()).Should().BeFalse("B's token is dead even though A is still paired");
        (await a.IsPairingLiveAsync()).Should().BeTrue("A's pairing is untouched");
    }

    [Fact]
    public async Task IsPairingLive_VerifyRoundTripAfterRestartReadsStale()
    {
        // A fresh gateway pair is live; after the daemon behind it is disposed
        // (a restart drops every in-memory token), a re-verify of the same
        // gateway must read stale — the chip and /host on then offer to re-pair
        // instead of no-opping on a dead token.
        await using var daemon = new HostDaemon(
            new BridgeConfig { PairSecret = Secret, HostExec = new HostExecPolicy { Default = "allow", Deny = [.. HostExecPolicy.SampleDeny] } },
            new HostIdentity("", SudoPolicy.Ask, null), _workDir, GetFreePort());
        daemon.Start();
        var gw = new HostGateway($"http://127.0.0.1:{daemon.Port}", "restart-me", _workDir, Secret);
        (await gw.PairAsync()).Should().BeNull();
        (await gw.IsPairingLiveAsync()).Should().BeTrue();

        // Simulate a daemon restart: revoke the session (new daemon = empty
        // session table; revoke is the closest in-process stand-in).
        daemon.RevokeSession("restart-me");
        (await gw.IsPairingLiveAsync()).Should().BeFalse();
    }

    // ── Daemon status + idle-token expiry ───────────────────────

    [Fact]
    public async Task Status_Endpoint_ReportsDaemonWithoutLeakingTokens()
    {
        using var http = new HttpClient();
        var json = await http.GetStringAsync($"http://127.0.0.1:{_daemon.Port}/status");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("ok").GetBoolean().Should().BeTrue();
        root.GetProperty("daemon").GetBoolean().Should().BeTrue();
        root.GetProperty("port").GetInt32().Should().Be(_daemon.Port);
        root.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("paired_sessions").GetInt32().Should().Be(0);
        json.Should().NotContain("token");
    }

    [Fact]
    public async Task Status_Endpoint_CountsPairedSessions()
    {
        var gw = NewGateway("status-count");
        (await gw.PairAsync()).Should().BeNull();

        using var http = new HttpClient();
        var json = await http.GetStringAsync($"http://127.0.0.1:{_daemon.Port}/status");
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        doc.RootElement.GetProperty("paired_sessions").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Run_IdleTokenExpired_FailsClosed()
    {
        using var shortIdentity = new HostIdentity("", SudoPolicy.Ask, null);
        await using var shortDaemon = new HostDaemon(
            new BridgeConfig { PairSecret = Secret, HostExec = new HostExecPolicy { Default = "allow" } },
            shortIdentity, _workDir, GetFreePort(), idleTimeout: TimeSpan.FromMilliseconds(50));
        shortDaemon.Start();

        var gw = new HostGateway($"http://127.0.0.1:{shortDaemon.Port}", "gw-idle", _workDir, Secret);
        (await gw.PairAsync()).Should().BeNull();

        await Task.Delay(200);

        // The stale pairing dies on its next call, fail closed — same shape
        // as token rotation.
        var answer = await gw.RunAsync(
            new HostExecRequest("echo after-idle", null, false, false),
            (_, _, _) => Task.FromResult(HostConsentDecision.AllowOnce),
            CancellationToken.None);
        answer.Should().Contain("pairing is stale");
    }

    [Fact]
    public async Task Run_ActivityRefreshesIdleClock()
    {
        using var identity = new HostIdentity("", SudoPolicy.Ask, null);
        await using var daemon = new HostDaemon(
            new BridgeConfig { PairSecret = Secret, HostExec = new HostExecPolicy { Default = "allow" } },
            identity, _workDir, GetFreePort(), idleTimeout: TimeSpan.FromSeconds(4));
        daemon.Start();

        var gw = new HostGateway($"http://127.0.0.1:{daemon.Port}", "gw-idle-active", _workDir, Secret);
        (await gw.PairAsync()).Should().BeNull();

        // Each exec lands well inside the idle window but the cumulative
        // elapsed time exceeds it (asserted below): validated activity must
        // keep the pairing alive. Margins are generous because the exec
        // round-trip itself drifts when the full suite runs in parallel.
        var started = DateTime.UtcNow;
        for (var i = 0; i < 8; i++)
        {
            await Task.Delay(500);
            var answer = await gw.RunAsync(
                new HostExecRequest($"echo tick-{i}", null, false, false),
                (_, _, _) => Task.FromResult(HostConsentDecision.AllowOnce),
                CancellationToken.None);
            answer.Should().Contain($"tick-{i}");
        }
        (DateTime.UtcNow - started).Should().BeGreaterThan(TimeSpan.FromSeconds(4),
            "the loop must outlive the idle window for the refresh to mean anything");
    }

    // ── Consent vocabulary ────────────────────────────────────────────

    [Theory]
    [InlineData("y", HostConsentDecision.AllowOnce)]
    [InlineData("allow once", HostConsentDecision.AllowOnce)]
    [InlineData("HOST_EXEC_APPROVE", HostConsentDecision.AllowOnce)]
    [InlineData("a", HostConsentDecision.AllowAll)]
    [InlineData("allow all (this session)", HostConsentDecision.AllowAll)]
    [InlineData("n", HostConsentDecision.DenyOnce)]
    [InlineData("deny", HostConsentDecision.DenyOnce)]
    [InlineData("HOST_EXEC_DENY", HostConsentDecision.DenyOnce)]
    [InlineData("!", HostConsentDecision.DenyAll)]
    [InlineData("deny all (stop host mode)", HostConsentDecision.DenyAll)]
    [InlineData("", HostConsentDecision.DenyOnce)]
    [InlineData("maybe", HostConsentDecision.DenyOnce)]
    [InlineData(null, HostConsentDecision.DenyOnce)]
    public void HostConsent_MapsSharedVocabulary(string? answer, HostConsentDecision expected) =>
        HostConsent.Map(answer).Should().Be(expected);

    [Fact]
    public void StandingOrders_NullUntilPaired()
    {
        // BuildStandingOrders returns null when unpaired — the /host on flow
        // relies on this to skip injection.
        var gw = NewGateway("orders-test");
        gw.BuildStandingOrders().Should().BeNull();
    }

    // ── Daemon restart = stale pairing (the /host on deadlock) ───────

    [Fact]
    public async Task DaemonRestart_PairingIsStale_AndRePairRecovers()
    {
        // Session tokens live only in daemon memory: a restart invalidates the
        // agent's token while IsPaired (a local flag) stays true. The gateway
        // must report the pairing as not live, and re-pairing must recover —
        // otherwise /host on no-ops on the dead token forever.
        var port = GetFreePort();
        var gw = new HostGateway($"http://127.0.0.1:{port}", "gw-restart", _workDir, Secret);
        await using (var daemon = new HostDaemon(
            new BridgeConfig { PairSecret = Secret, HostExec = new HostExecPolicy { Default = "allow", Deny = [.. HostExecPolicy.SampleDeny] } },
            new HostIdentity("", SudoPolicy.Ask, null), _workDir, port))
        {
            daemon.Start();
            (await gw.PairAsync()).Should().BeNull();
            (await gw.IsPairingLiveAsync()).Should().BeTrue();

            await gw.RunAsync(
                new HostExecRequest("echo before-restart", null, false, false),
                (_, _, _) => Task.FromResult(HostConsentDecision.AllowOnce),
                CancellationToken.None);
        }

        // Daemon went down and came back on the same port: the old token died
        // with it, but the gateway's local IsPaired flag never noticed.
        // While the daemon is unreachable we do NOT call the pairing dead
        // (a network blip must not revoke it) — exec fails closed anyway.
        (await gw.IsPairingLiveAsync()).Should().BeTrue();

        await using (var daemon = new HostDaemon(
            new BridgeConfig { PairSecret = Secret, HostExec = new HostExecPolicy { Default = "allow", Deny = [.. HostExecPolicy.SampleDeny] } },
            new HostIdentity("", SudoPolicy.Ask, null), _workDir, port))
        {
            daemon.Start();
            // The restarted daemon answers /status but knows zero sessions:
            // the pairing is stale even though IsPaired is still true.
            gw.IsPaired.Should().BeTrue();
            (await gw.IsPairingLiveAsync()).Should().BeFalse();

            // /host on's recovery path: drop the stale gateway, pair fresh.
            (await gw.PairAsync()).Should().BeNull();
            (await gw.IsPairingLiveAsync()).Should().BeTrue();
            var answer = await gw.RunAsync(
                new HostExecRequest("echo after-restart", null, false, false),
                (_, _, _) => Task.FromResult(HostConsentDecision.AllowOnce),
                CancellationToken.None);
            answer.Should().Contain("after-restart");
        }
    }

    // ── Container → loopback daemon Host-header routing ─────────────

    [Theory]
    [InlineData("http://host.docker.internal:7476", "127.0.0.1:7476")]
    [InlineData("http://HOST.DOCKER.INTERNAL:7476", "127.0.0.1:7476")]
    [InlineData("http://host.docker.internal", "127.0.0.1")]
    [InlineData("http://127.0.0.1:7476", null)]
    [InlineData("http://localhost:7476", null)]
    [InlineData("http://example.com:7476", null)]
    public void HostHeaderOverride_RewritesDockerGatewayToLoopback(string baseUrl, string? expected)
    {
        // The daemon binds http://127.0.0.1:<port>/ only; HttpListener 404s
        // requests whose Host: header names the Docker gateway instead. The
        // client must rewrite the header (TCP path unchanged) so container
        // pairing reaches the loopback daemon.
        HostDaemonClient.ResolveHostHeaderOverride(baseUrl).Should().Be(expected);
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
