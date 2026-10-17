using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace OpenMono.HostBridge;

/// <summary>
/// Lifecycle for the stock in-container agent exposing ACP: spawn a detached
/// <c>docker compose run</c> of the unmodified <c>agent</c> service with
/// <c>--acp-only</c> and loopback port publish, wait for discovery, and stop
/// what we started on exit. Mirrors the flag set <c>openmono agent</c>
/// (sandbox) already uses — git passthrough, endpoint mapping, lockfile port.
/// </summary>
public sealed class AgentContainer
{
    public const string ContainerName = "openmono-host-bridge";
    public const int ContainerAcpPort = 7475;

    private readonly string _composeFile;
    private readonly string _workDir;
    private readonly TextWriter _log;

    public AgentContainer(string composeFile, string workDir, TextWriter log)
    {
        _composeFile = composeFile;
        _workDir = workDir;
        _log = log;
    }

    public sealed record LlmSettings(string Endpoint, string ApiKey, string Gateway, string Search, string Scrape);

    public static LlmSettings ReadSettings()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".openmono", "settings.json");
        if (!File.Exists(path))
            return new LlmSettings("", "", "", "", "");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var llm = root.TryGetProperty("llm", out var llmEl) ? llmEl : (JsonElement?)null;
            var web = root.TryGetProperty("web", out var webEl) ? webEl : (JsonElement?)null;
            return new LlmSettings(
                Str(llm, "endpoint"),
                Str(llm, "api_key"),
                Str(web, "gateway"),
                Str(web, "search"),
                Str(web, "scrape"));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new LlmSettings("", "", "", "", "");
        }
    }

    public async Task<string> EnsureAsync(int hostPort, bool fresh, CancellationToken ct)
    {
        RequireDocker();
        if (IsRunning() && !fresh)
        {
            _log.WriteLine("[bridge] reusing running bridge container.");
            return $"http://127.0.0.1:{hostPort}";
        }
        if (IsPresent())
        {
            _log.WriteLine("[bridge] removing stale bridge container...");
            await RunDockerAsync("rm -f " + ContainerName, ct);
        }

        var settings = ReadSettings();
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
            throw new InvalidOperationException(
                "No LLM endpoint configured in ~/.openmono/settings.json. " +
                "Run: openmono config set llm.endpoint http://<relay-host>:<port> " +
                "(and llm.api_key), then retry.");

        var endpoint = MapEndpoint(settings.Endpoint);
        var gateway = MapGateway(settings.Gateway);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var args = new StringBuilder();
        args.Append($"compose -f \"{_composeFile}\" run -d --name {ContainerName} ");
        args.Append($"--user {Uid()}:{Gid()} ");
        args.Append($"-p 127.0.0.1:{hostPort}:{ContainerAcpPort} ");
        args.Append($"-v \"{_workDir}:/workspace\" ");
        args.Append($"-v \"{home}/.openmono:/home/agent/.openmono\" ");
        AppendGitMounts(args, home);
        args.Append("-e HOME=/home/agent ");
        args.Append($"-e WORKSPACE=\"{_workDir}\" ");
        args.Append($"-e OPENMONO_ENDPOINT={endpoint} ");
        string? apiKeyValue = null;
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            // Pass the key via the child process environment (-e NAME with the
            // value in process env), never on the docker command line where
            // it would be visible in ps output.
            args.Append("-e OPENMONO_API_KEY ");
            apiKeyValue = settings.ApiKey;
        }
        if (!string.IsNullOrWhiteSpace(gateway))
            args.Append($"-e OPENMONO_WEB_GATEWAY={gateway} ");
        if (!string.IsNullOrWhiteSpace(settings.Search))
            args.Append($"-e OPENMONO_WEB_SEARCH={settings.Search} ");
        if (!string.IsNullOrWhiteSpace(settings.Scrape))
            args.Append($"-e OPENMONO_WEB_SCRAPE={settings.Scrape} ");
        args.Append($"-e HOST_ACP_PORT={hostPort} ");
        args.Append($"-e OPENMONO_HOST_OS={(OperatingSystem.IsMacOS() ? "Darwin" : "Linux")} ");
        args.Append("-e GIT_TERMINAL_PROMPT=0 ");
        args.Append("agent --acp-only --acp-port 7475");

        _log.WriteLine("[bridge] starting ACP agent container...");
        var rc = await RunDockerAsync(args.ToString(), ct, apiKeyValue);
        if (rc != 0)
            throw new InvalidOperationException("docker compose run failed — is the Docker daemon running?");
        return $"http://127.0.0.1:{hostPort}";
    }

    public async Task StopIfSpawnedAsync(bool weSpawned, CancellationToken ct)
    {
        if (!weSpawned)
            return;
        try
        {
            await RunDockerAsync("rm -f " + ContainerName, ct);
        }
        catch (Exception)
        {
        }
    }

    public bool WasRunning() => IsRunning();

    private bool IsRunning()
    {
        try
        {
            var psi = new ProcessStartInfo("docker", $"inspect -f {{{{.State.Running}}}} {ContainerName}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return false;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);
            return process.ExitCode == 0 && output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool IsPresent()
    {
        try
        {
            var psi = new ProcessStartInfo("docker", $"inspect {ContainerName}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return false;
            process.WaitForExit(10_000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<int> RunDockerAsync(string args, CancellationToken ct, string? apiKeyValue = null)
    {
        var psi = new ProcessStartInfo("docker", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workDir,
        };
        if (!string.IsNullOrEmpty(apiKeyValue))
            psi.Environment["OPENMONO_API_KEY"] = apiKeyValue;
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start docker");
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (!string.IsNullOrWhiteSpace(stdout))
            _log.WriteLine(stdout.TrimEnd());
        if (!string.IsNullOrWhiteSpace(stderr))
            _log.WriteLine(stderr.TrimEnd());
        return process.ExitCode;
    }

    private static void RequireDocker()
    {
        try
        {
            var psi = new ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("failed to start docker");
            process.WaitForExit(15_000);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Docker daemon is not reachable (docker info failed).");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException("docker is not available on PATH.", ex);
        }
    }

    private static void AppendGitMounts(StringBuilder args, string home)
    {
        if (File.Exists(Path.Combine(home, ".gitconfig")))
            args.Append($"-v \"{home}/.gitconfig:/home/agent/.gitconfig:ro\" ");
        if (Directory.Exists(Path.Combine(home, ".ssh")))
            args.Append($"-v \"{home}/.ssh:/home/agent/.ssh:ro\" ");
        var creds = Path.Combine(home, ".git-credentials");
        if (File.Exists(creds))
            args.Append($"-v \"{creds}:/home/agent/.git-credentials:ro\" ");
        var sshAuth = Environment.GetEnvironmentVariable("SSH_AUTH_SOCK");
        if (!string.IsNullOrWhiteSpace(sshAuth))
        {
            try
            {
                var info = new FileInfo(sshAuth);
                if (info.Exists)
                    args.Append($"-v \"{sshAuth}:/ssh-agent\" -e SSH_AUTH_SOCK=/ssh-agent ");
            }
            catch (Exception)
            {
            }
        }
        args.Append("-e GIT_SSH_COMMAND=\"ssh -o StrictHostKeyChecking=accept-new -o BatchMode=yes\" ");
    }

    /// <summary>
    /// Rewrites a host-side loopback LLM endpoint for the agent container.
    ///
    /// Strata-style endpoints carry the OpenAI base path (/v1) and serve
    /// host-native on every OS (Linux default, see docs/STRATA.md), so loopback
    /// becomes the host gateway on macOS and Linux alike — preserving the port
    /// and path so :8080/v1 (or a custom --port) keeps working.
    ///
    /// Legacy llama.cpp endpoints (no base path): on Linux the compose network
    /// serves the model as <c>llama-server</c> (container port 7474, fixed); on
    /// macOS inference runs natively on the Mac host (Metal), so the container
    /// reaches it through Docker Desktop's <c>host.docker.internal</c>,
    /// preserving the native port (configurable via inference.port).
    /// Anything non-loopback passes through untouched.
    /// </summary>
    internal static string MapEndpoint(string endpoint)
    {
        // Strata (host-native, any OS): the /v1 base path is the tell. Keep the
        // port + path, swap only the host to the container's route to the host.
        if (IsLoopbackHttp(endpoint) &&
            Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
            uri.AbsolutePath.StartsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return $"http://host.docker.internal:{uri.Port}{uri.PathAndQuery}";
        }

        return IsLoopbackHttp(endpoint)
            ? OperatingSystem.IsMacOS()
                ? $"http://host.docker.internal:{LoopbackPortOr(endpoint, "7474")}"
                : "http://llama-server:7474"
            : endpoint;
    }

    /// <summary>
    /// Same rewrite for the optional web gateway. Linux keeps the compose
    /// <c>caddy:8080</c> mapping; macOS targets the host via
    /// <c>host.docker.internal</c>, preserving the configured port.
    /// </summary>
    internal static string MapGateway(string gateway) =>
        string.IsNullOrWhiteSpace(gateway)
            ? ""
            : IsLoopbackHttp(gateway)
                ? OperatingSystem.IsMacOS()
                    ? $"http://host.docker.internal:{LoopbackPortOr(gateway, "8080")}"
                    : "http://caddy:8080"
                : gateway;

    private static bool IsLoopbackHttp(string value) =>
        value.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("http://127.0.0.1:", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("http://host.docker.internal:", StringComparison.OrdinalIgnoreCase);

    private static string LoopbackPortOr(string endpoint, string fallback)
    {
        try
        {
            var port = new Uri(endpoint).Port;
            return port > 0
                ? port.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static string Str(JsonElement? parent, string name)
    {
        if (parent is not { } el || el.ValueKind != JsonValueKind.Object)
            return "";
        return el.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? ""
            : "";
    }

    private static string Uid() => RunId("id -u");
    private static string Gid() => RunId("id -g");

    private static string RunId(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("/bin/bash", $"-c \"{args}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return "0";
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5_000);
            return output.Length > 0 ? output : "0";
        }
        catch (Exception)
        {
            return "0";
        }
    }
}
