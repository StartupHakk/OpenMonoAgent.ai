namespace OpenMono.Win.AgentHost;

// Windows launch configuration for the unchanged Linux agent container.
// Mirrors OpenMono.HostBridge.AgentContainer.EnsureAsync for the Windows case:
// same compose service (agent), same ACP publish on loopback, same volume and
// env shape, minus the parts that do not exist on Windows:
//   - OPENMONO_ENDPOINT points at host Foundry via host.docker.internal:7474
//     instead of the llama-server compose-network name.
//   - No --user uid:gid (meaningless on the Windows host; Docker Desktop
//     handles file ownership on the mount).
//   - OPENMONO_HOST_OS=Windows so in-container host-gateway resolution knows
//     the host OS.
//   - No llama-server/caddy profiles: inference is host-native Foundry.
// The API key is never embedded here; like upstream it travels in the child
// process environment (-e NAME with no value), never on the command line.
public sealed record WindowsStackConfig(
    string WorkDir,
    string HomeDir,
    string ComposeFile,
    string InferenceEndpoint,
    string? Model = null,
    int AcpHostPort = 7475,
    string? ApiKey = null,
    string? Gateway = null,
    string? Search = null,
    string? Scrape = null,
    string? VisionEnabled = null)
{
    public string ContainerEndpoint => WindowsContainerEndpoint.MapForContainer(InferenceEndpoint);
}

public static class WindowsContainerStack
{
    public const string AgentService = "agent";

    public const int ContainerAcpPort = 7475;

    public const string ContainerCommand = "agent --acp-only --acp-port 7475";

    public static string PortPublishArg(WindowsStackConfig config) =>
        $"-p 127.0.0.1:{config.AcpHostPort}:{ContainerAcpPort}";

    public static string VolumeArgs(WindowsStackConfig config) =>
        $"-v \"{config.WorkDir}:/workspace\" -v \"{config.HomeDir}/.openmono:/home/agent/.openmono\"";

    // -e flags with values baked in. OPENMONO_API_KEY is intentionally a bare
    // -e NAME: Docker reads the value from the child process environment.
    public static IReadOnlyList<string> EnvironmentArgs(WindowsStackConfig config)
    {
        var env = new List<string>
        {
            "-e HOME=/home/agent",
            $"-e WORKSPACE=\"{config.WorkDir}\"",
            $"-e OPENMONO_ENDPOINT={config.ContainerEndpoint}",
            "-e OPENMONO_HOST_OS=Windows",
            "-e GIT_TERMINAL_PROMPT=0",
            $"-e HOST_ACP_PORT={config.AcpHostPort}",
        };

        if (!string.IsNullOrWhiteSpace(config.Model))
        {
            env.Add($"-e OPENMONO_MODEL={config.Model}");
        }

        if (!string.IsNullOrWhiteSpace(config.ApiKey))
        {
            env.Add("-e OPENMONO_API_KEY");
        }

        if (!string.IsNullOrWhiteSpace(config.Gateway))
        {
            env.Add($"-e OPENMONO_WEB_GATEWAY={config.Gateway}");
        }

        if (!string.IsNullOrWhiteSpace(config.Search))
        {
            env.Add($"-e OPENMONO_WEB_SEARCH={config.Search}");
        }

        if (!string.IsNullOrWhiteSpace(config.Scrape))
        {
            env.Add($"-e OPENMONO_WEB_SCRAPE={config.Scrape}");
        }

        if (!string.IsNullOrWhiteSpace(config.VisionEnabled))
        {
            env.Add($"-e OPENMONO_VISION_ENABLED={config.VisionEnabled}");
        }

        return env;
    }

    public static string ToDockerArgs(WindowsStackConfig config) =>
        $"compose -f \"{config.ComposeFile}\" run -d --name openmono-agent "
        + PortPublishArg(config) + " "
        + VolumeArgs(config) + " "
        + string.Join(" ", EnvironmentArgs(config)) + " "
        + $"{AgentService} {ContainerCommand}";

    public static string AgentUrl(WindowsStackConfig config) =>
        $"http://127.0.0.1:{config.AcpHostPort}";

    public static string DiscoveryUrl(WindowsStackConfig config) =>
        AgentUrl(config) + "/api/v1/discovery";
}
