using System.Collections.Concurrent;
using System.Text.Json;
using OpenMono.Config;

namespace OpenMono.Llm;

/// <summary>
/// Caches one <see cref="ILlmClient"/> per backend so turns reuse the same HTTP client
/// (and its connection pool / request gate) instead of allocating a new one per request.
/// A client is invalidated — and disposed — when the backend's key or endpoint changes.
/// </summary>
public sealed class LlmClientPool : IDisposable
{
    private readonly ConcurrentDictionary<string, ILlmClient> _clients =
        new(StringComparer.OrdinalIgnoreCase);

    public ILlmClient GetOrCreate(BackendDefinition backend, string? apiKey, string? model)
    {
        var key = backend.Name;
        return _clients.GetOrAdd(key, _ => CreateClient(backend, apiKey, model));
    }

    /// <summary>Drops (and disposes) the cached client for <paramref name="name"/>.</summary>
    public void Invalidate(string name)
    {
        if (_clients.TryRemove(name, out var client))
            client.Dispose();
    }

    /// <summary>Injects a pre-built client (used by tests to bypass real HTTP client creation).</summary>
    public void Set(string name, ILlmClient client) => _clients[name] = client;

    public void Dispose()
    {
        foreach (var client in _clients.Values)
            client.Dispose();
        _clients.Clear();
    }

    private ILlmClient CreateClient(BackendDefinition backend, string? apiKey, string? model)
    {
        var llmConfig = new LlmConfig
        {
            Endpoint = backend.Endpoint.TrimEnd('/'),
            Model = string.IsNullOrWhiteSpace(model) ? backend.DefaultModel ?? "" : model,
            ApiKey = apiKey,
        };

        return backend.ProviderType.Equals("anthropic", StringComparison.OrdinalIgnoreCase)
            ? new AnthropicClient(new ProviderConfig
            {
                Name = backend.Name,
                ApiKey = apiKey,
                Endpoint = backend.Endpoint.TrimEnd('/'),
                Model = string.IsNullOrWhiteSpace(model) ? backend.DefaultModel ?? "" : model,
            })
            : new OpenAiCompatClient(llmConfig) { ApiKey = apiKey };
    }
}
