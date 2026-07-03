using System.Text;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Acp;
using OpenMono.Permissions;
using OpenMono.Session;
using OpenMono.Playbooks;
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

        public Task<bool> RequestPermissionAsync(string toolName, string summary, bool dangerous, CancellationToken ct)
        {
            PermissionCalls.Add((toolName, summary, dangerous));
            return Task.FromResult(PermissionResult);
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
