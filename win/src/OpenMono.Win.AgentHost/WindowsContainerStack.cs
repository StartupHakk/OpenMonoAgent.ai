namespace OpenMono.Win.AgentHost;

// What `openmono agent` on Windows hands to `docker compose`. This mirrors
// cmd_agent_sandbox in the bash launcher: same compose file, same `agent`
// service, `run --rm` as a non-root user, the same -e and git mounts, and
// the same compose interpolation variables. The deliberate differences:
//   - OPENMONO_ENDPOINT targets host Foundry via host.docker.internal (the
//     macOS native-inference rewrite), not the llama-server service name.
//   - --user is a fixed 1000:1000: Windows has no uid, and the image makes
//     /home/agent 1777 and /etc/passwd writable for an arbitrary uid.
//   - HOST_WORKSPACE_PATH carries the Windows path, as the VS Code
//     extension's DockerManager does, so agent.lock names the host folder.
//   - With --acp-only, ACP is published on 127.0.0.1 only (the host-bridge
//     AgentContainer shape) so the extension can attach.
// Secrets never appear in the plan: OPENMONO_API_KEY flows from the caller's
// environment through compose interpolation, as on Linux.
public sealed record AgentRunRequest(
    string WorkDir,
    string HomeDir,
    string ComposeFile,
    IReadOnlyList<string> AgentArgs,
    string Endpoint,
    string? Model = null,
    int? ContextTokens = null,
    bool? VisionEnabled = null,
    string? WebGateway = null,
    string? WebSearch = null,
    string? WebScrape = null,
    bool NoGitAuth = false,
    Func<string, bool>? FileExists = null,
    Func<string, bool>? DirectoryExists = null);

public sealed record AgentRunPlan(
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> DockerArgs,
    IReadOnlyList<string> Notes,
    int? AcpHostPort);

public static class WindowsContainerStack
{
    public const string AgentService = "agent";

    public const string ContainerUser = "1000:1000";

    public static AgentRunPlan Build(AgentRunRequest request)
    {
        var fileExists = request.FileExists ?? File.Exists;
        var dirExists = request.DirectoryExists ?? Directory.Exists;
        var notes = new List<string>();

        var containerEndpoint = WindowsContainerEndpoint.MapForContainer(request.Endpoint);

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WORKSPACE"] = ComposePath(request.WorkDir),
            ["HOME"] = ComposePath(request.HomeDir),
            ["OPENMONO_ENDPOINT"] = containerEndpoint,
        };

        if (request.ContextTokens is > 0)
        {
            env["CTX_SIZE"] = request.ContextTokens.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (request.VisionEnabled is { } vision)
        {
            env["OPENMONO_VISION_ENABLED"] = vision ? "1" : "0";
        }

        if (!string.IsNullOrWhiteSpace(request.WebGateway))
        {
            if (IsLoopback(request.WebGateway))
            {
                notes.Add("web.gateway points at a host-local Caddy, which the Windows launcher does not run; "
                    + "WebSearch/WebFetch use the agent's built-in fallbacks.");
            }
            else
            {
                env["OPENMONO_WEB_GATEWAY"] = request.WebGateway;
                if (!string.IsNullOrWhiteSpace(request.WebSearch))
                {
                    env["OPENMONO_WEB_SEARCH"] = request.WebSearch;
                }

                if (!string.IsNullOrWhiteSpace(request.WebScrape))
                {
                    env["OPENMONO_WEB_SCRAPE"] = request.WebScrape;
                }
            }
        }

        var args = new List<string>
        {
            "compose", "-f", request.ComposeFile, "run", "--rm",
            "--user", ContainerUser,
            "-e", "HOME=/home/agent",
            "-e", $"WORKSPACE={request.WorkDir}",
            "-e", $"HOST_WORKSPACE_PATH={request.WorkDir}",
        };

        if (!string.IsNullOrWhiteSpace(request.Model))
        {
            args.Add("-e");
            args.Add($"OPENMONO_MODEL={request.Model}");
        }

        if (!request.NoGitAuth)
        {
            AddGitArgs(args, request.HomeDir, fileExists, dirExists);
        }

        var acpPort = AcpPublishPort(request.AgentArgs);
        if (acpPort is { } port)
        {
            args.Add("-p");
            args.Add($"127.0.0.1:{port}:{port}");
            args.Add("-e");
            args.Add($"HOST_ACP_PORT={port}");
        }

        args.Add(AgentService);
        args.AddRange(request.AgentArgs);

        return new AgentRunPlan(env, args, notes, acpPort);
    }

    // ACP is published only when the container runs as an ACP server for an
    // IDE (`--acp-only`). The default TUI run publishes nothing, as on Linux.
    public static int? AcpPublishPort(IReadOnlyList<string> agentArgs)
    {
        if (!agentArgs.Contains("--acp-only", StringComparer.Ordinal))
        {
            return null;
        }

        for (var i = 0; i < agentArgs.Count - 1; i++)
        {
            if (agentArgs[i] == "--acp-port" && int.TryParse(agentArgs[i + 1], out var explicitPort) && explicitPort > 0)
            {
                return explicitPort;
            }
        }

        return WindowsAcpWiring.DefaultHostPort;
    }

    // Compose interpolates ${HOME} and ${WORKSPACE} into bind-mount sources;
    // forward slashes keep "${HOME}/.openmono" a well-formed Windows path.
    public static string ComposePath(string windowsPath) =>
        windowsPath.Replace('\\', '/').TrimEnd('/');

    private static void AddGitArgs(List<string> args, string homeDir, Func<string, bool> fileExists, Func<string, bool> dirExists)
    {
        var gitconfig = Path.Combine(homeDir, ".gitconfig");
        if (fileExists(gitconfig))
        {
            args.Add("-v");
            args.Add($"{ComposePath(gitconfig)}:/home/agent/.gitconfig:ro");
        }

        var ssh = Path.Combine(homeDir, ".ssh");
        if (dirExists(ssh))
        {
            args.Add("-v");
            args.Add($"{ComposePath(ssh)}:/home/agent/.ssh:ro");
        }

        var credentials = Path.Combine(homeDir, ".git-credentials");
        if (fileExists(credentials))
        {
            args.Add("-v");
            args.Add($"{ComposePath(credentials)}:/home/agent/.git-credentials:ro");
        }

        args.AddRange(
        [
            "-e", "GIT_SSH_COMMAND=ssh -o StrictHostKeyChecking=accept-new -o BatchMode=yes",
            "-e", "GIT_TERMINAL_PROMPT=0",
            "-e", "GIT_CONFIG_COUNT=1",
            "-e", "GIT_CONFIG_KEY_0=safe.directory",
            "-e", "GIT_CONFIG_VALUE_0=/workspace",
        ]);
    }

    private static bool IsLoopback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.IsLoopback || string.Equals(uri.Host, "host.docker.internal", StringComparison.OrdinalIgnoreCase));
}
