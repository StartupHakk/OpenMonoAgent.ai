using OpenMono.Llm;

namespace OpenMono.Tests.Llm;

public class LlmClientPoolTests
{
    private static BackendDefinition MakeBackend(string name = "test", string? model = null) =>
        new()
        {
            Name = name,
            Endpoint = "http://localhost:1/v1",
            DefaultModel = model,
            ProviderType = "openai-compat",
        };

    [Fact]
    public void GetOrCreate_SameBackend_ReturnsSameInstance()
    {
        using var pool = new LlmClientPool();

        var first = pool.GetOrCreate(MakeBackend("test"), "key", "model");
        var second = pool.GetOrCreate(MakeBackend("test"), "key", "model");

        Assert.Same(first, second);
    }

    [Fact]
    public void GetOrCreate_DifferentBackends_ReturnsDistinctInstances()
    {
        using var pool = new LlmClientPool();

        var a = pool.GetOrCreate(MakeBackend("a"), "key", "m");
        var b = pool.GetOrCreate(MakeBackend("b"), "key", "m");

        Assert.NotSame(a, b);
    }

    [Fact]
    public void GetOrCreate_BuilderPicksClientByProviderType()
    {
        using var pool = new LlmClientPool();

        var openAi = pool.GetOrCreate(
            new BackendDefinition { Name = "oa", Endpoint = "http://localhost:1", ProviderType = "openai-compat" },
            "key", null);
        var anthropic = pool.GetOrCreate(
            new BackendDefinition { Name = "an", Endpoint = "http://localhost:1", ProviderType = "anthropic" },
            "key", null);

        Assert.IsType<OpenAiCompatClient>(openAi);
        Assert.IsType<AnthropicClient>(anthropic);
    }

    [Fact]
    public void Set_OverridesExistingEntry()
    {
        using var pool = new LlmClientPool();

        var original = pool.GetOrCreate(MakeBackend("test"), "key", "m");
        var injected = new FakeLlmClient("injected");

        pool.Set("test", injected);

        Assert.Same(injected, pool.GetOrCreate(MakeBackend("test"), "key", "m"));
        Assert.NotSame(original, pool.GetOrCreate(MakeBackend("test"), "key", "m"));
    }

    [Fact]
    public void Invalidate_RemovesAndDisposesCachedClient()
    {
        using var pool = new LlmClientPool();
        var injected = new FakeLlmClient("injected");
        pool.Set("test", injected);

        pool.Invalidate("test");

        Assert.True(injected.Disposed);
        // Re-creating returns a fresh client, not the disposed one.
        Assert.IsType<OpenAiCompatClient>(pool.GetOrCreate(MakeBackend("test"), "key", "m"));
    }

    [Fact]
    public void Invalidate_UnknownName_IsNoop()
    {
        using var pool = new LlmClientPool();

        pool.Invalidate("does-not-exist"); // must not throw
    }

    [Fact]
    public void Dispose_DisposesAllCachedClients()
    {
        var a = new FakeLlmClient("a");
        var b = new FakeLlmClient("b");

        var pool = new LlmClientPool();
        pool.Set("a", a);
        pool.Set("b", b);

        pool.Dispose();

        Assert.True(a.Disposed);
        Assert.True(b.Disposed);
    }
}
