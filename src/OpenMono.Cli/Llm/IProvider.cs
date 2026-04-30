namespace OpenMono.Llm;

public interface IProvider
{
    string Name { get; }
    string[] SupportedModels { get; }
    ILlmClient CreateClient(ProviderConfig config);
    bool ValidateConfig(ProviderConfig config, out string? error);
}

public sealed record ProviderConfig
{
    public required string Name { get; init; }
    public string? ApiKey { get; init; }
    public string? Endpoint { get; init; }
    public string? Model { get; init; }
    public Dictionary<string, string> Options { get; init; } = [];
}
