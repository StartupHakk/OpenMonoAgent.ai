using OpenMono.Session;

namespace OpenMono.Llm;

/// <summary>
/// An <see cref="ILlmClient"/> that resolves the real client for each request from the
/// session's <c>ActiveBackend</c> → <see cref="BackendRegistry"/> → <see cref="LlmClientPool"/>.
///
/// <para>
/// Used by the interactive (TUI/terminal) path, which drives one long-lived
/// <c>ConversationLoop</c> for the whole session. Resolving per-request (instead of
/// per-turn) means a mid-session backend switch via <c>/models</c> takes effect on the
/// very next LLM call — even mid-turn — without rebuilding the loop. Each request
/// carries the session's current model via <c>LlmOptions.Model</c>, which the concrete
/// clients honor over their own configured model.
/// </para>
///
/// <para>
/// When no backend is active (or only the "default" one is), the original process-wide
/// client is returned, so behavior is identical to pre-backend-swap for single-backend
/// setups.
/// </para>
/// </summary>
public sealed class RoutingLlmClient : ILlmClient
{
    private readonly SessionState _session;
    private readonly BackendRegistry _registry;
    private readonly LlmClientPool _pool;
    private readonly ILlmClient _fallback;
    private bool _disposed;

    public RoutingLlmClient(
        SessionState session,
        BackendRegistry registry,
        LlmClientPool pool,
        ILlmClient fallback)
    {
        _session = session;
        _registry = registry;
        _pool = pool;
        _fallback = fallback;
    }

    public IAsyncEnumerable<StreamChunk> StreamChatAsync(
        IReadOnlyList<Message> messages,
        System.Text.Json.JsonElement? tools,
        LlmOptions options,
        CancellationToken ct)
    {
        ThrowIfDisposed();
        var (client, effectiveOptions) = _resolve(options);
        return client.StreamChatAsync(messages, tools, effectiveOptions, ct);
    }

    private (ILlmClient Client, LlmOptions Options) _resolve(LlmOptions options)
    {
        var name = _session.ActiveBackend;
        if (string.IsNullOrWhiteSpace(name)
            || name.Equals(BackendRegistry.DefaultBackendName, StringComparison.OrdinalIgnoreCase))
        {
            return (_fallback, options);
        }

        var backend = _registry.Get(name) ?? _registry.Get(BackendRegistry.DefaultBackendName);
        if (backend is null)
            return (_fallback, options);

        // Keep the session's model authoritative: override options.Model so the resolved
        // client (which may have been cached under a different model) sends the right one.
        var model = string.IsNullOrWhiteSpace(_session.Model) ? null : _session.Model;
        var client = _pool.GetOrCreate(backend, _registry.GetKey(backend.Name), model);

        var effective = string.IsNullOrEmpty(options.Model) ? model ?? "" : options.Model;
        if (effective == options.Model)
            return (client, options);
        return (client, options with { Model = effective });
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Note: the fallback client is owned by Program.cs; the pool (and thus every
        // pooled client) is also owned by Program.cs. We dispose neither here so the
        // process-wide clients stay alive for the ACP path and shutdown ordering.
    }
}
