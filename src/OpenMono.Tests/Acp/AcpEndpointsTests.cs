using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenMono.Acp;
using OpenMono.Config;
using OpenMono.Llm;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;
using Xunit;

namespace OpenMono.Tests.Acp;

public sealed class AcpEndpointsTests : IAsyncLifetime
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "openmono-endpoint-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly CancellationTokenSource _cts = new();
    private readonly HangingLlm _llm = new();
    private string? _origWorkspaceEnv;
    private string? _origAgentIdEnv;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private int _port;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        _port = GetFreePort();



        _origWorkspaceEnv = Environment.GetEnvironmentVariable("HOST_WORKSPACE_PATH");
        _origAgentIdEnv = Environment.GetEnvironmentVariable("ACP_AGENT_ID");
        Environment.SetEnvironmentVariable("HOST_WORKSPACE_PATH", _tempDir);
        Environment.SetEnvironmentVariable("ACP_AGENT_ID", "agt_endpoint_test");

        var cfg = new AppConfig { DataDirectory = _tempDir };
        cfg.Llm.Model = "test-model";
        var settings = new AcpServerSettings
        {
            Port = _port,
            SessionTtlHours = 1,
            SessionsDirectory = Path.Combine(_tempDir, "acp-sessions"),
        };

        var services = new ServiceCollection();
        services.AddSingleton(cfg);
        services.AddSingleton(settings);
        services.AddSingleton<ILlmClient>(_llm);
        services.AddSingleton(new ToolRegistry());

        var renderer = new TerminalRenderer();
        services.AddSingleton<IOutputSink>(renderer);
        services.AddSingleton<IInputReader>(renderer);
        services.AddSingleton<ILiveFeedback>(renderer);

        services.AddSingleton(sp => new AcpSessionStore(sp.GetRequiredService<AppConfig>(), sp.GetRequiredService<AcpServerSettings>(), startReaper: false));
        services.AddSingleton(sp => new AcpLockFileWriter(sp.GetRequiredService<AcpServerSettings>(), "/workspace"));
        services.AddSingleton(sp => new ConversationLoopFactory(
            sp.GetRequiredService<ILlmClient>(),
            sp.GetRequiredService<ToolRegistry>(),
            sp.GetRequiredService<AppConfig>(),
            sp.GetRequiredService<IOutputSink>(),
            sp.GetRequiredService<IInputReader>(),
            sp.GetRequiredService<ILiveFeedback>()));
        services.AddSingleton<AcpTurnRunnerFactory>();

        _app = AcpServer.Build(settings, services);
        await _app.StartAsync(_cts.Token);

        _client = new HttpClient { BaseAddress = new Uri($"http://localhost:{_port}") };
    }

    public async Task DisposeAsync()
    {
        _llm.Release();
        _client.Dispose();
        await _cts.CancelAsync();
        try { await _app.StopAsync(); } catch {  }
        await _app.DisposeAsync();

        Environment.SetEnvironmentVariable("HOST_WORKSPACE_PATH", _origWorkspaceEnv);
        Environment.SetEnvironmentVariable("ACP_AGENT_ID", _origAgentIdEnv);

        try { Directory.Delete(_tempDir, recursive: true); } catch {  }
    }



    [Fact]
    public async Task GetDiscovery_returns_agent_metadata()
    {
        var res = await _client.GetAsync("/api/v1/discovery");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await res.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("version").GetString().Should().Be("1.0.0");
        root.GetProperty("agent_id").GetString().Should().Be("agt_endpoint_test");
        root.GetProperty("host_workspace").GetString().Should().Be(_tempDir);
        root.GetProperty("container_workspace").GetString().Should().Be("/workspace");
        root.GetProperty("status").GetString().Should().Be("ready");
        root.GetProperty("uptime_seconds").GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }



    [Fact]
    public async Task PostSessions_returns_session_id_and_resolved_model()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/sessions", new { model = "gpt-4o" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("session_id").GetString().Should().StartWith("sess_");
        doc.RootElement.GetProperty("model").GetString().Should().Be("gpt-4o");
    }

    [Fact]
    public async Task PostSessions_falls_back_to_config_default_model_for_empty_body()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/sessions", new { });
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("model").GetString().Should().Be("test-model");
    }

    [Fact]
    public async Task PostSessions_ignores_extra_fields_like_client_tools_silently()
    {


        var body = new { model = "gpt-4o", client_tools = new[] { "FileRead", "Bash" } };
        var res = await _client.PostAsJsonAsync("/api/v1/sessions", body);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }



    [Fact]
    public async Task GetSession_returns_404_for_missing_id()
    {
        var res = await _client.GetAsync("/api/v1/sessions/sess_missing");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }



    [Fact]
    public async Task PostHost_returns_404_for_unknown_session()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/sessions/sess_missing/host", new { action = "on" });
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PostHost_rejects_bad_body_and_unknown_action()
    {
        var sid = await CreateSessionAsync();

        var raw = await _client.PostAsync($"/api/v1/sessions/{sid}/host",
            new StringContent("not json", System.Text.Encoding.UTF8, "application/json"));
        raw.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var bad = await _client.PostAsJsonAsync($"/api/v1/sessions/{sid}/host", new { action = "sideways" });
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostHost_on_without_daemon_answers_gracefully_not_4xx()
    {
        var sid = await CreateSessionAsync();

        // No host daemon env/secret in the test fixture: pairing must fail
        // closed as ok:false with guidance, never a crash or a 4xx.
        var origMode = Environment.GetEnvironmentVariable("OPENMONO_HOST_MODE");
        var origSecret = Environment.GetEnvironmentVariable("OPENMONO_HOST_PAIR_SECRET");
        var origUrl = Environment.GetEnvironmentVariable("OPENMONO_HOST_DAEMON_URL");
        Environment.SetEnvironmentVariable("OPENMONO_HOST_MODE", null);
        Environment.SetEnvironmentVariable("OPENMONO_HOST_PAIR_SECRET", null);
        // Dead port: even if the machine has a real pair secret on disk,
        // pairing deterministically fails on connect instead of racing a
        // developer's actually-running daemon.
        Environment.SetEnvironmentVariable("OPENMONO_HOST_DAEMON_URL", "http://127.0.0.1:1");
        try
        {
            var res = await _client.PostAsJsonAsync($"/api/v1/sessions/{sid}/host", new { action = "on" });
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
            doc.RootElement.GetProperty("host_enabled").GetBoolean().Should().BeFalse();
            doc.RootElement.GetProperty("error").GetString().Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENMONO_HOST_MODE", origMode);
            Environment.SetEnvironmentVariable("OPENMONO_HOST_PAIR_SECRET", origSecret);
            Environment.SetEnvironmentVariable("OPENMONO_HOST_DAEMON_URL", origUrl);
        }
    }

    [Fact]
    public async Task PostHost_off_is_idempotent_for_unpaired_session()
    {
        var sid = await CreateSessionAsync();

        for (var i = 0; i < 2; i++)
        {
            var res = await _client.PostAsJsonAsync($"/api/v1/sessions/{sid}/host", new { action = "off" });
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("ok").GetBoolean().Should().BeTrue();
            doc.RootElement.GetProperty("host_enabled").GetBoolean().Should().BeFalse();
        }

        var store = _app.Services.GetRequiredService<AcpSessionStore>();
        store.TryGet(sid)!.State.Meta.HostEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task GetSession_returns_200_with_metadata_for_existing_session()
    {
        var sid = await CreateSessionAsync();

        var res = await _client.GetAsync($"/api/v1/sessions/{sid}");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("session_id").GetString().Should().Be(sid);
        root.GetProperty("model").GetString().Should().Be("test-model");
        root.GetProperty("turn_count").GetInt32().Should().Be(0);
        root.GetProperty("plan_mode").GetBoolean().Should().BeTrue("new sessions default to plan mode (read-only)");
        root.GetProperty("host_enabled").GetBoolean().Should().BeFalse("new sessions are sandboxed; the extension reads this for the Host button state");
    }



    [Fact]
    public async Task GetMessages_returns_camelCase_history_with_toolCalls_folded_in()
    {
        var sid = await CreateSessionAsync();



        var store = _app.Services.GetRequiredService<AcpSessionStore>();
        var session = store.TryGet(sid)!;
        session.Messages.Add(new Message { Role = MessageRole.User, Content = "list files" });
        session.Messages.Add(new Message
        {
            Role = MessageRole.Assistant,
            Content = "Sure.",
            ToolCalls = new() { new ToolCall { Id = "call_1", Name = "ListDirectory", Arguments = "{\"path\":\".\"}" } },
        });
        session.Messages.Add(new Message
        {
            Role = MessageRole.Tool,
            ToolCallId = "call_1",
            ToolName = "ListDirectory",
            Content = "file1\nfile2",
        });
        store.Save(session);

        var res = await _client.GetAsync($"/api/v1/sessions/{sid}/messages");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = await res.Content.ReadAsStringAsync();
        raw.Should().Contain("\"toolCalls\":", "MUST be camelCase to match the extension's HistoryMessage type");
        raw.Should().NotContain("\"tool_calls\":");

        using var doc = JsonDocument.Parse(raw);
        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToArray();

        messages.Should().HaveCount(2, "the Tool message must be folded into the assistant's toolCalls, not surfaced separately");
        messages[0].GetProperty("role").GetString().Should().Be("user");
        messages[0].GetProperty("content").GetString().Should().Be("list files");

        messages[1].GetProperty("role").GetString().Should().Be("assistant");
        messages[1].GetProperty("content").GetString().Should().Be("Sure.");
        var calls = messages[1].GetProperty("toolCalls").EnumerateArray().ToArray();
        calls.Should().HaveCount(1);
        calls[0].GetProperty("id").GetString().Should().Be("call_1");
        calls[0].GetProperty("name").GetString().Should().Be("ListDirectory");
        calls[0].GetProperty("ok").GetBoolean().Should().BeTrue();
        calls[0].GetProperty("preview").GetString().Should().Be("file1\nfile2");
    }

    [Fact]
    public async Task GetMessages_returns_404_for_missing_session()
    {
        var res = await _client.GetAsync("/api/v1/sessions/sess_missing/messages");
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }



    [Fact]
    public async Task PostTurn_returns_409_when_session_lock_held()
    {
        var sid = await CreateSessionAsync();
        var store = _app.Services.GetRequiredService<AcpSessionStore>();
        var session = store.TryGet(sid)!;


        await session.TurnLock.WaitAsync();
        try
        {
            var res = await _client.PostAsJsonAsync($"/api/v1/sessions/{sid}/turn", new { message = "hi" });
            res.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var raw = await res.Content.ReadAsStringAsync();
            raw.Should().Contain("session_busy");
        }
        finally
        {
            session.TurnLock.Release();
        }
    }

    [Fact]
    public async Task PostTurn_returns_400_for_unknown_body_shape()
    {
        var sid = await CreateSessionAsync();
        var res = await _client.PostAsJsonAsync($"/api/v1/sessions/{sid}/turn", new { greeting = "hello?" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var raw = await res.Content.ReadAsStringAsync();
        raw.Should().Contain("invalid_body");
    }

    [Fact]
    public async Task PostTurn_with_abort_true_cancels_pending_pauses_and_returns_204()
    {
        var sid = await CreateSessionAsync();
        var store = _app.Services.GetRequiredService<AcpSessionStore>();
        var session = store.TryGet(sid)!;
        session.RegisterPause("perm_x", PendingResponseKind.Permission, "Bash|x");
        session.PendingIds.Should().NotBeEmpty();

        var res = await _client.PostAsJsonAsync($"/api/v1/sessions/{sid}/turn", new { abort = true });

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        session.PendingIds.Should().BeEmpty();
    }



    [Fact]
    public async Task DeleteSession_removes_session_from_store_and_returns_204()
    {
        var sid = await CreateSessionAsync();
        var store = _app.Services.GetRequiredService<AcpSessionStore>();
        store.TryGet(sid).Should().NotBeNull();

        var res = await _client.DeleteAsync($"/api/v1/sessions/{sid}");

        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
        store.TryGet(sid).Should().BeNull();
    }

    [Fact]
    public async Task DeleteSession_is_idempotent_for_missing_session()
    {
        var res = await _client.DeleteAsync("/api/v1/sessions/sess_missing");
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }



    [Fact]
    public async Task GetSessions_lists_workspace_sessions_with_digests()
    {
        var sid1 = await CreateSessionAsync();
        var store = _app.Services.GetRequiredService<AcpSessionStore>();
        var s1 = store.TryGet(sid1)!;
        s1.Messages.Add(new Message { Role = MessageRole.User, Content = "Fix the parser bug" });
        s1.TurnCount = 1;
        store.Save(s1);

        await CreateSessionAsync();

        var res = await _client.GetAsync("/api/v1/sessions");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var sessions = doc.RootElement.GetProperty("sessions").EnumerateArray().ToArray();
        sessions.Length.Should().BeGreaterThanOrEqualTo(2);

        var first = sessions.Single(e => e.GetProperty("session_id").GetString() == sid1);
        first.GetProperty("title").GetString().Should().Be("Fix the parser bug");
        first.GetProperty("turn_count").GetInt32().Should().Be(1);
        first.GetProperty("model").GetString().Should().Be("test-model");
        first.GetProperty("message_count").GetInt32().Should().BeGreaterThanOrEqualTo(1);
        first.GetProperty("started_at").GetString().Should().NotBeNullOrEmpty();
        first.GetProperty("last_activity_at").GetString().Should().NotBeNullOrEmpty();
    }

    private async Task<string> CreateSessionAsync()
    {
        var res = await _client.PostAsJsonAsync("/api/v1/sessions", new { });
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("session_id").GetString()!;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }





    private sealed class HangingLlm : ILlmClient
    {
        private readonly TaskCompletionSource _gate = new();

        public void Release() => _gate.TrySetResult();

        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages, JsonElement? toolDefs, LlmOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await _gate.Task.WaitAsync(ct);
            yield return new StreamChunk { IsComplete = true };
        }

        public void Dispose() => _gate.TrySetResult();
    }
}
