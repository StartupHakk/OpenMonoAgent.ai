using System.Diagnostics;

namespace OpenMono.Windows.Supervisor;

/// <summary>
/// Manages the Docker services stack (Caddy gateway, SearXNG, Scrapling) by
/// reference to the existing docker/ compose definitions plus the windows/
/// override. Never edits docker/ files. Never starts the containerized agent
/// or the containerized llama-server on Windows.
/// </summary>
public sealed class DockerComposeManager
{
    private readonly string _repoRoot;
    private readonly SupervisorConfig _config;

    public DockerComposeManager(string repoRoot, SupervisorConfig config)
    {
        _repoRoot = repoRoot;
        _config = config;
    }

    public string BaseComposeFile => Path.Combine(_repoRoot, "docker", "docker-compose.yml");
    public string WindowsOverrideFile => Path.Combine(_repoRoot, "windows", "docker", "docker-compose.windows.yml");

    public string BuildUpCommand() =>
        $"compose -f \"{BaseComposeFile}\" -f \"{WindowsOverrideFile}\" --profile full up -d caddy searxng scrapling";

    public async Task<ComposeResult> UpAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Starting web services (caddy, searxng, scrapling)...");
        var env = BuildEnvironment();
        return await RunComposeAsync(BuildUpCommand(), env, ct);
    }

    public async Task<ComposeResult> DownAsync(CancellationToken ct = default)
    {
        var env = BuildEnvironment();
        return await RunComposeAsync($"compose -f \"{BaseComposeFile}\" -f \"{WindowsOverrideFile}\" stop caddy searxng scrapling", env, ct);
    }

    public Dictionary<string, string> BuildEnvironment()
    {
        // Caddy upstream points at the NATIVE llama-server on the Windows host.
        // LLAMA_UPSTREAM uses host.docker.internal, matching the comment already
        // present in docker-compose.yml for this exact setup.
        return new Dictionary<string, string>
        {
            ["LLAMA_UPSTREAM"] = $"host.docker.internal:{_config.LlamaPort}",
            ["LLAMA_PORT"] = _config.LlamaPort.ToString(),
            ["GATEWAY_PORT"] = _config.GatewayPort.ToString(),
            ["WEB_SEARCH_ENABLED"] = "true",
            ["WEB_SCRAPE_ENABLED"] = "true",
        };
    }

    private static async Task<ComposeResult> RunComposeAsync(string args, Dictionary<string, string> env, CancellationToken ct)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            foreach (var (key, value) in env)
            {
                process.StartInfo.Environment[key] = value;
            }

            if (!process.Start())
            {
                return new ComposeResult(false, "Failed to start docker compose.");
            }

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            var error = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return new ComposeResult(process.ExitCode == 0, string.IsNullOrWhiteSpace(error) ? output : error);
        }
        catch (Exception ex)
        {
            return new ComposeResult(false, ex.Message);
        }
    }

    public sealed record ComposeResult(bool Ok, string Output);
}
