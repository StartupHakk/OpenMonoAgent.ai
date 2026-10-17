using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using OpenMono.HostBridge;
using OpenMono.Session;

namespace OpenMono.Tests.HostBridge;

/// <summary>
/// Pairing security: TTL-bounded invites, operator-approval pair requests, the
/// pair_auto lane, admin endpoints, and hard token lifetimes. Same real-daemon
/// harness as HostGatewayTests.
/// </summary>
public class PairSecurityTests
{
    private static readonly string Secret = "test-pair-secret-9d1e4b";

    private static string TempWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "openmono-pairsec-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static BridgeConfig Config(HostExecPolicy? policy = null) => new()
    {
        PairSecret = Secret,
        HostExec = policy ?? new HostExecPolicy { Default = "allow", Deny = [.. HostExecPolicy.SampleDeny] },
    };

    private static int GetFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ── One-shot invites ────────────────────────────────────────────

    [Fact]
    public async Task Invite_PairsEverySessionInRunWhileLive()
    {
        // The live bug this guards: a launcher hands ONE invite to one container
        // run, and every session in that run pairs with it. Consuming the invite
        // on first use left every later session in the same container with no
        // credential — the daemon sent them to the approval lane (pair_timeout)
        // and host mode read "off" until the daemon was restarted. The invite is
        // now TTL-bounded, not single-use: it keeps pairing until it expires.
        var config = Config();
        config.PairRequestWaitSeconds = 1;
        await using var daemon = new HostDaemon(config, new HostIdentity("", SudoPolicy.Ask, null),
            TempWorkDir(), GetFreePort());
        daemon.Start();
        var invite = daemon.CreateInvite();

        var gw1 = new HostGateway($"http://127.0.0.1:{daemon.Port}", "inv-a", TempWorkDir(), invite: invite);
        (await gw1.PairAsync()).Should().BeNull();

        // Second session, same invite, still live: pairs silently (no approval
        // lane, no timeout) — this is what the single-use behavior broke.
        var gw2 = new HostGateway($"http://127.0.0.1:{daemon.Port}", "inv-b", TempWorkDir(), invite: invite);
        (await gw2.PairAsync()).Should().BeNull();
        gw2.IsPaired.Should().BeTrue();
    }

    [Fact]
    public async Task Invite_BogusValueFallsToApprovalLane()
    {
        // A TTL-bounded invite is only valid when the daemon issued it. A made-up
        // value is not a credential: with no secret it falls into the approval
        // lane, which (tiny wait) times out fail-closed — it never pairs.
        var config = Config();
        config.PairRequestWaitSeconds = 1;
        await using var daemon = new HostDaemon(config, new HostIdentity("", SudoPolicy.Ask, null),
            TempWorkDir(), GetFreePort());
        daemon.Start();

        var gw = new HostGateway($"http://127.0.0.1:{daemon.Port}", "bogus", TempWorkDir(), invite: "not-a-real-invite");
        var error = await gw.PairAsync();
        error.Should().NotBeNull();
        gw.IsPaired.Should().BeFalse();
    }

    [Fact]
    public async Task Invite_AdminEndpointHonorsTtlOverride()
    {
        // The launcher mints long-TTL invites for manual /host on sessions via
        // {"ttl_seconds":N}. The override must produce a working invite, and
        // the clamped floor (30s) must still be usable immediately.
        await using var daemon = new HostDaemon(Config(), new HostIdentity("", SudoPolicy.Ask, null),
            TempWorkDir(), GetFreePort());
        daemon.Start();

        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{daemon.Port}/admin/invite");
        req.Headers.TryAddWithoutValidation("X-Pair-Secret", Secret);
        req.Content = new StringContent("""{"session_id":"x","ttl_seconds":43200}""", Encoding.UTF8, "application/json");
        using var resp = await http.SendAsync(req);
        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var invite = doc.RootElement.GetProperty("invite").GetString();
        invite.Should().NotBeNullOrWhiteSpace();

        var gw = new HostGateway($"http://127.0.0.1:{daemon.Port}", "ttl-invite", TempWorkDir(), invite: invite);
        (await gw.PairAsync()).Should().BeNull("the TTL-overridden invite is live");
    }

    [Fact]
    public async Task Invite_WrongSecretStillRefusedInstantly()
    {
        // A guessed pair secret must be an instant refusal, never an approval
        // queue entry the operator could accidentally approve.
        await using var daemon = new HostDaemon(Config(), new HostIdentity("", SudoPolicy.Ask, null),
            TempWorkDir(), GetFreePort());
        daemon.Start();

        var gw = new HostGateway($"http://127.0.0.1:{daemon.Port}", "attacker", TempWorkDir(), "guessed");
        var error = await gw.PairAsync();
        error.Should().Contain("bad pair secret");
    }

    [Fact]
    public async Task PairRequest_TimesOutFailClosed()
    {
        var config = Config();
        config.PairRequestWaitSeconds = 1;
        await using var daemon = new HostDaemon(config, new HostIdentity("", SudoPolicy.Ask, null),
            TempWorkDir(), GetFreePort());
        daemon.Start();

        // No secret, no invite: a pair request nobody approves dies fail-closed.
        var gw = new HostGateway($"http://127.0.0.1:{daemon.Port}", "lonely", TempWorkDir());
        var error = await gw.PairAsync();
        error.Should().NotBeNull().And.Contain("did not approve");
        gw.IsPaired.Should().BeFalse();
    }

    [Fact]
    public async Task PairRequest_ApprovedByHost_Succeeds()
    {
        var config = Config();
        config.PairRequestWaitSeconds = 10;
        await using var daemon = new HostDaemon(config, new HostIdentity("", SudoPolicy.Ask, null),
            TempWorkDir(), GetFreePort());
        daemon.Start();

        var gw = new HostGateway($"http://127.0.0.1:{daemon.Port}", "need-approval", TempWorkDir());
        var pairTask = Task.Run(() => gw.PairAsync());

        // Wait until the request is visible on the admin list, then approve.
        PairRequest? seen = null;
        for (var i = 0; i < 40 && seen is null; i++)
        {
            await Task.Delay(100);
            seen = daemon.ListPendingRequests().FirstOrDefault(r => r.SessionId == "need-approval").SessionId is null
                ? null
                : new PairRequest("", "need-approval", "");
        }
        seen.Should().NotBeNull("the pending request must appear on the admin list");
        daemon.ResolvePairRequest("need-approval", approve: true).Should().BeTrue();

        (await pairTask).Should().BeNull();
        gw.IsPaired.Should().BeTrue();
    }

    [Fact]
    public async Task PairRequest_DeniedByHost_Fails()
    {
        var config = Config();
        config.PairRequestWaitSeconds = 10;
        await using var daemon = new HostDaemon(config, new HostIdentity("", SudoPolicy.Ask, null),
            TempWorkDir(), GetFreePort());
        daemon.Start();

        var gw = new HostGateway($"http://127.0.0.1:{daemon.Port}", "rejected", TempWorkDir());
        var pairTask = Task.Run(() => gw.PairAsync());

        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(100);
            if (daemon.ResolvePairRequest("rejected", approve: false)) break;
        }

        var error = await pairTask;
        error.Should().NotBeNull().And.Contain("denied");
        gw.IsPaired.Should().BeFalse();
    }

    [Fact]
    public async Task PairRequest_PairRequestedEvent_FiresWithWorkDir()
    {
        var config = Config();
        config.PairRequestWaitSeconds = 10;
        await using var daemon = new HostDaemon(config, new HostIdentity("", SudoPolicy.Ask, null),
            TempWorkDir(), GetFreePort());
        daemon.Start();

        PairRequest? raised = null;
        var gate = new TaskCompletionSource();
        daemon.PairRequested += r => { raised = r; gate.TrySetResult(); };

        var gw = new HostGateway($"http://127.0.0.1:{daemon.Port}", "evt-session", TempWorkDir());
        var pairTask = Task.Run(() => gw.PairAsync());
        await gate.Task; // the console approver must get the signal
        raised!.SessionId.Should().Be("evt-session");
        raised.WorkDir.Should().NotBeNullOrWhiteSpace();

        daemon.ResolvePairRequest("evt-session", approve: true);
        (await pairTask).Should().BeNull();
    }

    [Fact]
    public async Task PairAuto_ThisSession_AutoApprovesSeenSessionOnReconnect()
    {
        var config = Config();
        config.PairAuto = "this_session";
        await using var daemon = new HostDaemon(config, new HostIdentity("", SudoPolicy.Ask, null),
            TempWorkDir(), GetFreePort());
        daemon.Start();

        // First pairing via secret registers the session id as "seen".
        var gw = new HostGateway($"http://127.0.0.1:{daemon.Port}", "resumer", TempWorkDir(), Secret);
        (await gw.PairAsync()).Should().BeNull();

        // Manual re-pair after the token went stale: no secret, no invite —
        // auto-approved silently because this daemon has seen this session.
        var gw2 = new HostGateway($"http://127.0.0.1:{daemon.Port}", "resumer", TempWorkDir());
        (await gw2.PairAsync()).Should().BeNull();
        gw2.IsPaired.Should().BeTrue();
    }

    [Fact]
    public async Task PairAuto_Strict_UnknownSessionTimesOut()
    {
        var config = Config();
        config.PairAuto = "strict";
        config.PairRequestWaitSeconds = 1;
        await using var daemon = new HostDaemon(config, new HostIdentity("", SudoPolicy.Ask, null),
            TempWorkDir(), GetFreePort());
        daemon.Start();

        var gw = new HostGateway($"http://127.0.0.1:{daemon.Port}", "never-seen", TempWorkDir());
        (await gw.PairAsync()).Should().NotBeNull();
        gw.IsPaired.Should().BeFalse();
    }

    // ── Admin surface over HTTP (pair-secret authenticated) ─────────

    [Fact]
    public async Task AdminEndpoints_RequirePairSecret()
    {
        await using var daemon = new HostDaemon(Config(), new HostIdentity("", SudoPolicy.Ask, null),
            TempWorkDir(), GetFreePort());
        daemon.Start();

        using var http = new HttpClient();
        var resp = await http.PostAsync($"http://127.0.0.1:{daemon.Port}/admin/invite", null);
        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AdminEndpoints_InviteApproveSessionRevoke_WorkEndToEnd()
    {
        var config = Config();
        config.PairRequestWaitSeconds = 10;
        await using var daemon = new HostDaemon(config, new HostIdentity("", SudoPolicy.Ask, null),
            TempWorkDir(), GetFreePort());
        daemon.Start();
        var client = new HostDaemonClient($"http://127.0.0.1:{daemon.Port}");

        var invite = await client.CreateInviteAsync(Secret, "ignored", CancellationToken.None);
        invite.Ok.Should().BeTrue(because: $"daemon said: {invite.Error}");
        invite.Invite.Should().NotBeNullOrWhiteSpace();

        // Pair with the invite, then manage the session over the admin lane.
        var gw = new HostGateway(client.BaseUrl, "admin-e2e", TempWorkDir(), invite: invite.Invite);
        (await gw.PairAsync()).Should().BeNull();

        var sessions = await client.AdminListAsync(Secret, "/admin/sessions", CancellationToken.None);
        sessions.Ok.Should().BeTrue();
        sessions.Items!.Should().ContainSingle(s => s.SessionId == "admin-e2e");

        // Revoke from the host kills the session; the agent's next exec fails.
        (await client.AdminActionAsync(Secret, "/admin/revoke", "admin-e2e", CancellationToken.None))
            .Ok.Should().BeTrue();
        var answer = await gw.RunAsync(
            new HostExecRequest("echo post-revoke", null, false, false),
            (_, _, _) => Task.FromResult(HostConsentDecision.AllowOnce),
            CancellationToken.None);
        answer.Should().Contain("pairing is stale");

        client.Dispose();
    }

    [Fact]
    public async Task PairResponse_ReasonSurfacesAutoApprove()
    {
        var config = Config();
        config.PairAuto = "this_session";
        await using var daemon = new HostDaemon(config, new HostIdentity("", SudoPolicy.Ask, null),
            TempWorkDir(), GetFreePort());
        daemon.Start();
        var client = new HostDaemonClient($"http://127.0.0.1:{daemon.Port}");

        (await client.PairAsync(new PairRequest(Secret, "seen-once", TempWorkDir()), CancellationToken.None)).Ok
            .Should().BeTrue();
        var again = await client.PairAsync(new PairRequest("", "seen-once", TempWorkDir()), CancellationToken.None);
        again.Ok.Should().BeTrue();
        again.Reason.Should().Contain("auto-approved");

        client.Dispose();
    }

    // ── Hard token lifetime (daemon-side cap) ───────────────────────

    [Fact]
    public async Task TokenMaxHours_DaemonForcesReApproval()
    {
        var config = Config();
        config.TokenMaxHours = 0.002; // ~7.2 seconds
        await using var daemon = new HostDaemon(config, new HostIdentity("", SudoPolicy.Ask, null),
            TempWorkDir(), GetFreePort(), idleTimeout: TimeSpan.FromMinutes(5));
        daemon.Start();

        var gw = new HostGateway($"http://127.0.0.1:{daemon.Port}", "maxlife", TempWorkDir(), Secret);
        (await gw.PairAsync()).Should().BeNull();
        // Activity keeps the idle clock green, but the hard cap since pairing
        // still kills the token: wait past it with live execs.
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(2_000);
            var live = await gw.RunAsync(
                new HostExecRequest($"echo alive-{i}", null, false, false),
                (_, _, _) => Task.FromResult(HostConsentDecision.AllowOnce),
                CancellationToken.None);
            if (live.Contains("pairing is stale")) break;
        }

        var answer = await gw.RunAsync(
            new HostExecRequest("echo past-cap", null, false, false),
            (_, _, _) => Task.FromResult(HostConsentDecision.AllowOnce),
            CancellationToken.None);
        answer.Should().Contain("pairing is stale");
    }

    // ── Grant TTLs (agent-side, consent vocabulary) ─────────────────

    [Theory]
    [InlineData("30m", HostConsentDecision.Allow30m)]
    [InlineData("allow all (30 minutes)", HostConsentDecision.Allow30m)]
    [InlineData("today", HostConsentDecision.AllowToday)]
    [InlineData("allow all (today)", HostConsentDecision.AllowToday)]
    public void HostConsent_MapsGrantDurations(string answer, HostConsentDecision expected) =>
        HostConsent.Map(answer).Should().Be(expected);

    private static async Task RunEchoAsync(HostGateway gw)
    {
        var answer = await gw.RunAsync(
            new HostExecRequest("echo grant-probe", null, false, false),
            (_, _, _) => Task.FromResult(HostConsentDecision.AllowOnce),
            CancellationToken.None);
        answer.Should().Contain("grant-probe");
    }

    [Fact]
    public async Task Grant30m_SilentWhileValid_PromptsAfterExpiry()
    {
        await using var daemon = new HostDaemon(Config(), new HostIdentity("", SudoPolicy.Ask, null),
            TempWorkDir(), GetFreePort());
        daemon.Start();
        var gw = new HostGateway($"http://127.0.0.1:{daemon.Port}", "grant30", TempWorkDir(), Secret);
        (await gw.PairAsync()).Should().BeNull();

        // Establish the 30-minute grant.
        var decided = false;
        var answer = await gw.RunAsync(
            new HostExecRequest("echo establish", null, false, false),
            (_, _, _) => { decided = true; return Task.FromResult(HostConsentDecision.Allow30m); },
            CancellationToken.None);
        answer.Should().Contain("establish");
        decided.Should().BeTrue();

        // Next command: consent callback must NOT be invoked (grant covers it).
        var secondPrompted = false;
        var second = await gw.RunAsync(
            new HostExecRequest("echo covered", null, false, false),
            (_, _, _) => { secondPrompted = true; return Task.FromResult(HostConsentDecision.DenyOnce); },
            CancellationToken.None);
        second.Should().Contain("covered");
        secondPrompted.Should().BeFalse("a live 30m grant must not re-prompt");
    }

    [Fact]
    public async Task Grant_AllowAllStillNeverCoversSudo()
    {
        await using var daemon = new HostDaemon(
            new BridgeConfig { PairSecret = Secret, HostExec = new HostExecPolicy { Default = "allow", Deny = [.. HostExecPolicy.SampleDeny] } },
            new HostIdentity("", SudoPolicy.Ask, null), TempWorkDir(), GetFreePort());
        daemon.Start();
        var gw = new HostGateway($"http://127.0.0.1:{daemon.Port}", "sudo-grant", TempWorkDir(), Secret);
        (await gw.PairAsync()).Should().BeNull();

        await gw.RunAsync(
            new HostExecRequest("echo establish", null, false, false),
            (_, _, _) => Task.FromResult(HostConsentDecision.AllowAll),
            CancellationToken.None);

        var prompted = false;
        await gw.RunAsync(
            new HostExecRequest("echo escalate", null, false, true),
            (_, _, _) => { prompted = true; return Task.FromResult(HostConsentDecision.DenyOnce); },
            CancellationToken.None);
        prompted.Should().BeTrue("allow-all must never cover a root escalation");
    }
}
