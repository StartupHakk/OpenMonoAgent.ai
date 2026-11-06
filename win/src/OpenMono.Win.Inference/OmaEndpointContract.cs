namespace OpenMono.Win.Inference;

// How OpenMono.Cli talks to inference, mirrored for the Windows host so both
// backends honor the same contract. Source of truth in the repo:
//   - OpenMono.Llm.OpenAiCompatClient posts to {Endpoint}/v1/chat/completions.
//   - OpenMono.Llm.ModelCatalog reads {Endpoint}/v1/models (data[].id).
//   - Program.cs polls {Endpoint}/health and {Endpoint}/props for readiness
//     and model auto-detect, and ConfigLoader strips one trailing "/v1".
// The Windows host runs the same llama-server natively instead of in a
// compose service, so only the base URL changes.
public static class OmaEndpointContract
{
    // OMA default root (AppConfig.Llm.Endpoint, ProviderRegistry fallbacks).
    public const string OmaDefaultEndpoint = "http://localhost:7474";

    public const string ChatCompletionsPath = "/v1/chat/completions";

    public const string ModelsPath = "/v1/models";

    public const string HealthPath = "/health";

    public const string PropsPath = "/props";

    // Env overrides honored by ConfigLoader.ApplyEnvironmentOverrides.
    public const string EndpointEnvVar = "OPENMONO_ENDPOINT";

    public const string ModelEnvVar = "OPENMONO_MODEL";

    public const string ApiKeyEnvVar = "OPENMONO_API_KEY";

    // Reduce any endpoint spelling to the root every call site appends to.
    // Mirrors ConfigLoader.NormalizeEndpoint: trim slashes, drop one /v1.
    public static string TrimRoot(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return string.Empty;
        }

        var trimmed = endpoint.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^3].TrimEnd('/');
        }

        return trimmed;
    }

    public static string ChatCompletionsUrl(string endpointRoot) =>
        TrimRoot(endpointRoot) + ChatCompletionsPath;

    public static string ModelsUrl(string endpointRoot) =>
        TrimRoot(endpointRoot) + ModelsPath;

    public static string HealthUrl(string endpointRoot) =>
        TrimRoot(endpointRoot) + HealthPath;
}
