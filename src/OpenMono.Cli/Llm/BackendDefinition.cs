using System.Text.Json.Serialization;

namespace OpenMono.Llm;

/// <summary>
/// A named inference backend (endpoint + default model + provider type). This is the
/// non-secret half of a backend configuration. The API key, when present, lives only in
/// the process's <see cref="BackendRegistry"/> key store — it is never part of this record
/// and is never written to <c>backends.json</c>.
/// </summary>
public sealed record BackendDefinition
{
    /// <summary>Stable, unique name (used as the key in the registry and client pool).</summary>
    public required string Name { get; init; }

    /// <summary>Optional human-friendly display name (e.g. a relay's nickname).</summary>
    [JsonPropertyName("nickname")]
    public string? Nickname { get; init; }

    /// <summary>OpenAI-compatible base URL (no trailing slash required).</summary>
    public required string Endpoint { get; init; }

    /// <summary>Model name sent when the session has no explicit model set.</summary>
    [JsonPropertyName("default_model")]
    public string? DefaultModel { get; init; }

    /// <summary>How to build the client. "anthropic" → <see cref="AnthropicClient"/>,
    /// anything else → <see cref="OpenAiCompatClient"/> (covers llama.cpp, Strata, Ollama,
    /// OpenAI, and any OpenAI-compatible relay).</summary>
    [JsonPropertyName("provider_type")]
    public string ProviderType { get; init; } = "openai-compat";

    /// <summary>Local server (llama.cpp / Strata) that needs no API key.</summary>
    [JsonPropertyName("is_local")]
    public bool IsLocal { get; init; }
}
