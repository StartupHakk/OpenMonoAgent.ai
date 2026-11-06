using OpenMono.Config;
using OpenMono.Llm;
using OpenMono.Win.Foundry;

namespace OpenMono.Win.AgentHost;

// Entry point for hosting OpenMono.Cli against the Foundry endpoint.
// Same contract the CLI uses for llama-server (root URL + model name),
// only the base URL points at Foundry on 127.0.0.1:7474.
public sealed class WinAgentHost
{
    public string ModelAlias { get; }

    public WinAgentHost(string modelAlias)
    {
        ModelAlias = modelAlias;
    }

    public LlmConfig BuildLlmConfig() => new()
    {
        Endpoint = FoundryConstants.DefaultLoopbackEndpoint,
        Model = ModelAlias,
    };

    public OpenAiCompatClient CreateLlmClient(string? apiKey = null) =>
        new(BuildLlmConfig()) { ApiKey = apiKey };

    // Mirrors ConfigLoader env overrides so child processes and the CLI
    // agree on the endpoint without editing settings files.
    public static void ApplyEnvironment(string modelAlias)
    {
        var (endpoint, model) = OmaEndpointContract.ForFoundry(modelAlias);
        Environment.SetEnvironmentVariable(OmaEndpointContract.EndpointEnvVar, endpoint);
        Environment.SetEnvironmentVariable(OmaEndpointContract.ModelEnvVar, model);
    }
}
