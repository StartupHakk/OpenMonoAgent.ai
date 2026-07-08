using System.Text.Json;
using OpenMono.Config;
using OpenMono.Session;
using OpenMono.Utils;

namespace OpenMono.Llm;

public enum ModelTier
{
    Executive,
    Operator,
    Coder
}

public sealed class MultiModelLlmClient : ILlmClient, IDisposable
{
    private readonly ILlmClient _executive;
    private readonly ILlmClient? _operator;
    private readonly ILlmClient? _coder;
    private readonly bool _hasTiers;
    private bool _disposed;

    public ILlmClient Executive => _executive;
    public ILlmClient? Operator => _operator;
    public ILlmClient? Coder => _coder;
    public bool HasTiers => _hasTiers;

    public MultiModelLlmClient(
        ILlmClient executive,
        ILlmClient? @operator = null,
        ILlmClient? coder = null)
    {
        _executive = executive;
        _operator = @operator;
        _coder = coder;
        _hasTiers = @operator is not null || coder is not null;
    }

    public ILlmClient GetClient(ModelTier tier) => tier switch
    {
        ModelTier.Operator when _operator is not null => _operator,
        ModelTier.Coder when _coder is not null => _coder,
        ModelTier.Operator when _coder is not null => _coder,
        _ => _executive
    };

    public LlmOptions GetOptions(ModelTier tier, LlmOptions baseOptions)
    {
        if (tier == ModelTier.Executive)
            return baseOptions;

        return baseOptions with
        {
            Temperature = tier == ModelTier.Operator ? 0.3 : 0.2,
            MaxTokens = Math.Min(baseOptions.MaxTokens, 8192),
        };
    }

    public IAsyncEnumerable<StreamChunk> StreamChatAsync(
        IReadOnlyList<Message> messages,
        JsonElement? tools,
        LlmOptions options,
        CancellationToken ct)
    {
        return _executive.StreamChatAsync(messages, tools, options, ct);
    }

    public IAsyncEnumerable<StreamChunk> StreamChatAsync(
        ModelTier tier,
        IReadOnlyList<Message> messages,
        JsonElement? tools,
        LlmOptions options,
        CancellationToken ct)
    {
        var client = GetClient(tier);
        var tierOptions = GetOptions(tier, options);

        if (tier != ModelTier.Executive)
        {
            Log.Info($"[MULTI_MODEL] Routing to {tier} tier");
        }

        return client.StreamChatAsync(messages, tools, tierOptions, ct);
    }

    public ILlmClient CreateTierClient(ModelTier tier, LlmConfig fallbackConfig)
    {
        if (tier == ModelTier.Executive)
            return _executive;

        var existing = GetClient(tier);
        if (existing is not null)
            return existing;

        return _executive;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _executive.Dispose();
        _operator?.Dispose();
        _coder?.Dispose();
    }
}

