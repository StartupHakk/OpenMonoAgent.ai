using System.Runtime.CompilerServices;
using OpenMono.Llm;
using OpenMono.Session;

namespace OpenMono.Tests.Llm;

public class RoutingLlmClientTests
{
    private sealed class Harness
    {
        public Harness(string? activeBackend = null, string? sessionModel = null)
        {
            Session = new SessionState { ActiveBackend = activeBackend, Model = sessionModel };
            Registry = new BackendRegistry();
            Pool = new LlmClientPool();
            Fallback = new FakeLlmClient("fallback");
            Work = new FakeLlmClient("work");
            Default = new FakeLlmClient("default");
            Routing = new RoutingLlmClient(Session, Registry, Pool, Fallback);
        }

        public SessionState Session { get; }
        public BackendRegistry Registry { get; }
        public LlmClientPool Pool { get; }
        public FakeLlmClient Fallback { get; }
        public FakeLlmClient Work { get; }
        public FakeLlmClient Default { get; }
        public RoutingLlmClient Routing { get; }

        /// <summary>Registers a "work" backend and pre-seeds its pool client with <see cref="Work"/>.</summary>
        public void SeedWork(string providerType = "openai-compat")
        {
            Registry.Add(new BackendDefinition
            {
                Name = "work",
                Endpoint = "http://localhost:1",
                ProviderType = providerType,
            });
            Pool.Set("work", Work);
        }

        /// <summary>Registers a "default" backend and pre-seeds its pool client with <see cref="Default"/>.</summary>
        public void SeedDefault()
        {
            Registry.Add(new BackendDefinition
            {
                Name = BackendRegistry.DefaultBackendName,
                Endpoint = "http://localhost:1",
            });
            Pool.Set(BackendRegistry.DefaultBackendName, Default);
        }
    }

    private static async Task<string?> FirstTextAsync(
        ILlmClient client,
        LlmOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new LlmOptions();
        await foreach (var chunk in client.StreamChatAsync([], null, options, ct))
        {
            if (!string.IsNullOrEmpty(chunk.TextDelta))
                return chunk.TextDelta;
        }
        return null;
    }

    [Fact]
    public async Task NoActiveBackend_UsesFallback()
    {
        var h = new Harness(); // ActiveBackend = null
        h.SeedWork();

        var text = await FirstTextAsync(h.Routing);

        Assert.Equal("fallback", text);
        Assert.Equal(1, h.Fallback.CallCount);
        Assert.Equal(0, h.Work.CallCount);
    }

    [Fact]
    public async Task DefaultBackend_UsesFallback()
    {
        var h = new Harness(activeBackend: BackendRegistry.DefaultBackendName);
        h.SeedWork();

        var text = await FirstTextAsync(h.Routing);

        Assert.Equal("fallback", text);
        Assert.Equal(1, h.Fallback.CallCount);
        Assert.Equal(0, h.Work.CallCount);
    }

    [Fact]
    public async Task RegisteredActiveBackend_UsesPoolClient()
    {
        var h = new Harness(activeBackend: "work");
        h.SeedWork();

        var text = await FirstTextAsync(h.Routing);

        Assert.Equal("work", text);
        Assert.Equal(1, h.Work.CallCount);
        Assert.Equal(0, h.Fallback.CallCount);
    }

    [Fact]
    public async Task UnregisteredBackend_And_NoDefault_UsesFallback()
    {
        var h = new Harness(activeBackend: "ghost");
        h.SeedWork(); // only "work" registered, no "default"

        var text = await FirstTextAsync(h.Routing);

        Assert.Equal("fallback", text);
        Assert.Equal(1, h.Fallback.CallCount);
        Assert.Equal(0, h.Work.CallCount);
    }

    [Fact]
    public async Task UnregisteredBackend_WithDefaultRegistered_UsesDefaultPoolClient()
    {
        var h = new Harness(activeBackend: "ghost");
        h.SeedDefault(); // "default" registered + seeded

        var text = await FirstTextAsync(h.Routing);

        Assert.Equal("default", text);
        Assert.Equal(1, h.Default.CallCount);
        Assert.Equal(0, h.Fallback.CallCount);
    }

    [Fact]
    public async Task SessionModel_OVERRIDES_EmptyOptionsModel()
    {
        var h = new Harness(activeBackend: "work", sessionModel: "session-model");
        h.SeedWork();

        var text = await FirstTextAsync(h.Routing, new LlmOptions { Model = "" });

        Assert.Equal("work", text);
        Assert.Equal("session-model", h.Work.LastModel);
    }

    [Fact]
    public async Task NonEmptyOptionsModel_Wins_Over_SessionModel()
    {
        var h = new Harness(activeBackend: "work", sessionModel: "session-model");
        h.SeedWork();

        var text = await FirstTextAsync(h.Routing, new LlmOptions { Model = "option-model" });

        Assert.Equal("work", text);
        Assert.Equal("option-model", h.Work.LastModel);
    }

    [Fact]
    public async Task NoModelAnywhere_SendsEmptyModel()
    {
        var h = new Harness(activeBackend: "work"); // no session model
        h.SeedWork();

        var text = await FirstTextAsync(h.Routing, new LlmOptions { Model = "" });

        Assert.Equal("work", text);
        Assert.Equal("", h.Work.LastModel);
    }

    [Fact]
    public async Task Dispose_Then_StreamChat_Throws()
    {
        var h = new Harness();

        h.Routing.Dispose();

        // ThrowIfDisposed runs on the first MoveNext (async iterator), not at the call site.
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
        {
            await foreach (var _ in h.Routing.StreamChatAsync([], null, new LlmOptions(), CancellationToken.None))
                ;
        });
    }

    [Fact]
    public void Dispose_Does_Not_Dispose_Fallback()
    {
        var h = new Harness();
        var fallback = h.Fallback;

        h.Routing.Dispose();

        Assert.False(fallback.Disposed);
    }
}
