using System.Text;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Acp;
using OpenMono.HostBridge;
using OpenMono.Permissions;
using OpenMono.Session;
using OpenMono.Playbooks;
using OpenMono.Tests.HostBridge;
using Xunit;

namespace OpenMono.Tests.Acp;

public sealed class AcpUserInteractionForwarderTests
{


    [Fact]
    public async Task RequestPermissionAsync_emits_permission_request_event_then_throws()
    {
        var (forwarder, session, body) = BuildForwarder();

        PendingUserResponseException? thrown = null;
        try
        {
            await forwarder.RequestPermissionAsync("Bash", "rm -rf node_modules", dangerous: true, CancellationToken.None);
        }
        catch (PendingUserResponseException ex) { thrown = ex; }

        thrown.Should().NotBeNull("the forwarder must unwind the loop after emitting");

        var ev = ParseSingleEvent(body);
        ev.EventName.Should().Be("permission_request");
        ev.Data.GetProperty("id").GetString().Should().Be(thrown!.PauseId);
        ev.Data.GetProperty("tool").GetString().Should().Be("Bash");
        ev.Data.GetProperty("summary").GetString().Should().Be("rm -rf node_modules");
        ev.Data.GetProperty("dangerous").GetBoolean().Should().BeTrue();

        thrown.Kind.Should().Be(PendingResponseKind.Permission);
        thrown.PauseId.Should().StartWith("perm_");
        session.PendingIds.Should().ContainSingle().Which.Should().Be(thrown.PauseId);
    }

    [Fact]
    public async Task RequestUserInputAsync_emits_user_input_request_event_then_throws()
    {
        var (forwarder, session, body) = BuildForwarder();

        PendingUserResponseException? thrown = null;
        try
        {
            await forwarder.RequestUserInputAsync("Which encryption algorithm?", CancellationToken.None);
        }
        catch (PendingUserResponseException ex) { thrown = ex; }

        thrown.Should().NotBeNull();

        var ev = ParseSingleEvent(body);
        ev.EventName.Should().Be("user_input_request");
        ev.Data.GetProperty("id").GetString().Should().Be(thrown!.PauseId);
        ev.Data.GetProperty("question").GetString().Should().Be("Which encryption algorithm?");

        thrown.Kind.Should().Be(PendingResponseKind.UserInput);
        thrown.PauseId.Should().StartWith("ask_");
        session.PendingIds.Should().ContainSingle().Which.Should().Be(thrown.PauseId);
    }

    [Fact]
    public async Task RequestPermissionAsync_carries_dangerous_false_when_not_destructive()
    {
        var (forwarder, _, body) = BuildForwarder();

        try { await forwarder.RequestPermissionAsync("FileRead", "src/auth.ts", dangerous: false, CancellationToken.None); }
        catch (PendingUserResponseException) {  }

        ParseSingleEvent(body).Data.GetProperty("dangerous").GetBoolean().Should().BeFalse();
    }



    [Fact]
    public async Task Adapter_maps_RequestPermission_true_to_PermissionResponse_Allow()
    {
        var fake = new FakeInteraction { PermissionResult = true };
        var adapter = new AcpInputReaderAdapter(fake);

        var result = await adapter.AskPermissionAsync("Bash", "ls -la", CancellationToken.None);

        result.Should().Be(PermissionResponse.Allow);
        fake.PermissionCalls.Should().ContainSingle().Which.tool.Should().Be("Bash");
    }

    [Fact]
    public async Task Adapter_maps_RequestPermission_false_to_PermissionResponse_Deny()
    {
        var fake = new FakeInteraction { PermissionResult = false };
        var adapter = new AcpInputReaderAdapter(fake);

        var result = await adapter.AskPermissionAsync("Bash", "ls -la", CancellationToken.None);

        result.Should().Be(PermissionResponse.Deny);
    }

    [Fact]
    public async Task Adapter_marks_destructive_Bash_commands_as_dangerous()
    {
        var fake = new FakeInteraction { PermissionResult = true };
        var adapter = new AcpInputReaderAdapter(fake);

        await adapter.AskPermissionAsync("Bash", "rm -rf /tmp/junk", CancellationToken.None);
        await adapter.AskPermissionAsync("Bash", "git push --force origin main", CancellationToken.None);
        await adapter.AskPermissionAsync("Bash", "ls -la", CancellationToken.None);
        await adapter.AskPermissionAsync("FileWrite", "src/foo.cs", CancellationToken.None);

        fake.PermissionCalls.Should().HaveCount(4);
        fake.PermissionCalls[0].dangerous.Should().BeTrue("rm -rf");
        fake.PermissionCalls[1].dangerous.Should().BeTrue("git push --force");
        fake.PermissionCalls[2].dangerous.Should().BeFalse("plain ls");
        fake.PermissionCalls[3].dangerous.Should().BeTrue("FileWrite always touches disk");
    }

    [Fact]
    public async Task Adapter_passes_through_AskUser_and_converts_null_to_empty()
    {
        var fake = new FakeInteraction { UserInputResult = "AES-256-GCM" };
        var adapter = new AcpInputReaderAdapter(fake);

        (await adapter.AskUserAsync("which algorithm?", CancellationToken.None)).Should().Be("AES-256-GCM");

        fake.UserInputResult = null;
        (await adapter.AskUserAsync("again?", CancellationToken.None)).Should().Be(string.Empty);
    }



    [Fact]
    public async Task RequestPermissionAsync_remembered_grant_resolves_without_pause()
    {
        var (forwarder, session, body) = BuildForwarder();
        // Grant stored under a differently-cased key: normalization must hit.
        session.Store.Remember("Bash", allow: true, GrantScope.Session);

        var (allow, scope) = await forwarder.RequestPermissionAsync("bash", "rm -rf node_modules", dangerous: true, CancellationToken.None);

        allow.Should().BeTrue();
        scope.Should().Be("session");
        session.PendingIds.Should().BeEmpty("no pause must register for a remembered grant");
        body.Length.Should().Be(0, "no SSE event must emit for a remembered grant");
    }

    [Fact]
    public async Task RequestPermissionAsync_remembered_deny_resolves_without_pause()
    {
        var (forwarder, session, _) = BuildForwarder();
        session.Store.Remember("Bash", allow: false, GrantScope.Session);

        var (allow, _) = await forwarder.RequestPermissionAsync("Bash", "anything", dangerous: false, CancellationToken.None);

        allow.Should().BeFalse();
        session.PendingIds.Should().BeEmpty();
    }

    // ── needs_password on host_exec user_input_request events ─────────
    // The client renders the full-screen sudo-password modal (long-lived
    // pause) only when the command needs root AND the paired daemon will not
    // run it unattended (allow_sudo != allow). Every other shape degrades to
    // the plain consent row.

    [Fact]
    public async Task host_exec_as_root_with_ask_daemon_sets_needs_password()
    {
        await using var gw = new PairedTestGateway("sess_np_1", "ask");
        var (forwarder, session, body) = BuildForwarder();
        session.State.Id = "sess_np_1";

        try { await forwarder.RequestUserInputAsync(SudoEnvelope(), CancellationToken.None); }
        catch (PendingUserResponseException) { }

        var ev = ParseSingleEvent(body);
        ev.EventName.Should().Be("user_input_request");
        ev.Data.GetProperty("kind").GetString().Should().Be("host_exec");
        ev.Data.GetProperty("as_root").GetBoolean().Should().BeTrue();
        ev.Data.GetProperty("needs_password").GetBoolean().Should().BeTrue(
            "root escalation + daemon allow_sudo=ask ⇒ the modal must open");
    }

    [Fact]
    public async Task host_exec_as_root_with_allow_daemon_needs_no_password()
    {
        await using var gw = new PairedTestGateway("sess_np_2", "allow");
        var (forwarder, session, body) = BuildForwarder();
        session.State.Id = "sess_np_2";

        try { await forwarder.RequestUserInputAsync(SudoEnvelope(), CancellationToken.None); }
        catch (PendingUserResponseException) { }

        ParseSingleEvent(body).Data.GetProperty("needs_password").GetBoolean().Should().BeFalse(
            "allow_sudo=allow runs unattended — no password modal");
    }

    [Fact]
    public async Task host_exec_non_root_needs_no_password_even_with_ask_daemon()
    {
        await using var gw = new PairedTestGateway("sess_np_3", "ask");
        var (forwarder, session, body) = BuildForwarder();
        session.State.Id = "sess_np_3";

        var plain = "HOST_EXEC: {\"command\": \"ls -la\", \"background\": false, \"as_root\": false}";
        try { await forwarder.RequestUserInputAsync(plain, CancellationToken.None); }
        catch (PendingUserResponseException) { }

        var ev = ParseSingleEvent(body);
        ev.Data.GetProperty("as_root").GetBoolean().Should().BeFalse();
        ev.Data.GetProperty("needs_password").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task host_exec_as_root_unpaired_session_needs_no_password()
    {
        var (forwarder, session, body) = BuildForwarder(); // no gateway registered

        try { await forwarder.RequestUserInputAsync(SudoEnvelope(), CancellationToken.None); }
        catch (PendingUserResponseException) { }

        ParseSingleEvent(body).Data.GetProperty("needs_password").GetBoolean().Should().BeFalse(
            "unpaired ⇒ no gateway ⇒ plain consent row; the daemon fails closed as before");
    }

    private static string SudoEnvelope() =>
        "HOST_EXEC: {\"command\": \"echo as-root-ok\", \"timeout_ms\": 5000, \"background\": false, \"as_root\": true}";

    /// <summary>Paired gateway against a canned daemon, registered under the
    /// session id and unregistered on dispose (the registry is static).</summary>
    private sealed class PairedTestGateway : IAsyncDisposable
    {
        private readonly HostGateway _gw;
        private readonly FakeHostDaemon _daemon;
        private readonly string _sessionId;

        public PairedTestGateway(string sessionId, string sudoPolicy)
        {
            _sessionId = sessionId;
            _daemon = new FakeHostDaemon("correct-horse", sudoPolicy);
            _gw = new HostGateway(_daemon.BaseUrl, sessionId, "/tmp", _daemon.Secret);
            _gw.PairAsync().GetAwaiter().GetResult();
            HostGatewayRegistry.Register(sessionId, _gw);
        }

        public async ValueTask DisposeAsync()
        {
            HostGatewayRegistry.Unregister(_sessionId);
            await _gw.UnpairAsync();
            _gw.Dispose();
            _daemon.Dispose();
        }
    }

    private static (AcpUserInteractionForwarder forwarder, AcpSession session, MemoryStream body) BuildForwarder()
    {
        var body = new MemoryStream();
        var writer = new SseWriter(body, CancellationToken.None);
        var session = new AcpSession { State = new SessionState { Id = "sess_test", StartedAt = DateTime.UtcNow, Model = "test-model" } };
        var forwarder = new AcpUserInteractionForwarder(session, writer, TimeSpan.FromMinutes(10));
        return (forwarder, session, body);
    }

    private static (string EventName, JsonElement Data) ParseSingleEvent(MemoryStream body)
    {
        var text = Encoding.UTF8.GetString(body.ToArray()).Trim();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().StartWith("event: ");
        lines[1].Should().StartWith("data: ");
        var name = lines[0]["event: ".Length..];
        var data = JsonDocument.Parse(lines[1]["data: ".Length..]).RootElement.Clone();
        return (name, data);
    }

    private sealed class FakeInteraction : IAcpUserInteraction
    {
        public bool PermissionResult { get; set; }
        public bool PlaybookApprovalResult { get; set; }
        public bool ToggleModeResult { get; set; }
        public string? UserInputResult { get; set; }
        public List<(string tool, string summary, bool dangerous)> PermissionCalls { get; } = new();
        public List<PlaybookToolPlan> PlaybookApprovalCalls { get; } = new();
        public List<string> ToggleModeCalls { get; } = new();
        public List<string> UserInputCalls { get; } = new();

        public Task<(bool Allow, string Scope)> RequestPermissionAsync(string toolName, string summary, bool dangerous, CancellationToken ct)
        {
            PermissionCalls.Add((toolName, summary, dangerous));
            return Task.FromResult((PermissionResult, "once"));
        }

        public Task<bool> RequestPlaybookApprovalAsync(PlaybookToolPlan plan, CancellationToken ct)
        {
            PlaybookApprovalCalls.Add(plan);
            return Task.FromResult(PlaybookApprovalResult);
        }

        public Task<bool> RequestToggleModeAsync(string reason, CancellationToken ct)
        {
            ToggleModeCalls.Add(reason);
            return Task.FromResult(ToggleModeResult);
        }

        public Task<string?> RequestUserInputAsync(string question, CancellationToken ct)
        {
            UserInputCalls.Add(question);
            return Task.FromResult(UserInputResult);
        }
    }
}
