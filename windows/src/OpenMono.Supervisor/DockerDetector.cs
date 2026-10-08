using System.Diagnostics;
using System.Text.RegularExpressions;

namespace OpenMono.Windows.Supervisor;

/// <summary>
/// Docker Desktop detection and version parsing. Docker Desktop with the WSL2
/// backend hosts Caddy, SearXNG, and Scrapling. Native llama-server never runs
/// in Docker on Windows. All parsing is pure for unit tests.
/// </summary>
public static class DockerDetector
{
    public sealed record DockerStatus(
        bool EngineAvailable,
        bool ComposeAvailable,
        string? ServerVersion,
        string? Detail);

    public static async Task<DockerStatus> DetectAsync(CancellationToken ct = default)
    {
        var (engineOk, engineOut) = await TryRunAsync("docker", "info --format {{.Server.Version}}", ct);
        if (!engineOk)
        {
            return new DockerStatus(false, false, null, "Docker engine is not reachable. Install or start Docker Desktop.");
        }

        var version = ParseDockerVersion(engineOut);
        var (composeOk, _) = await TryRunAsync("docker", "compose version --short", ct);
        return new DockerStatus(true, composeOk, version, composeOk ? null : "Docker engine is up but compose is unavailable.");
    }

    public static string? ParseDockerVersion(string output)
    {
        var match = Regex.Match(output.Trim(), @"\d+\.\d+\.\d+");
        return match.Success ? match.Value : null;
    }

    /// <summary>
    /// Pure helper: decides whether the Docker first run step can be skipped.
    /// Missing Docker is skippable with a clear explanation of what is lost
    /// (server backed web search and scraping, gateway remains off).
    /// </summary>
    public static string SkippedExplanation() =>
        "Docker Desktop was skipped. Web search falls back to built in DuckDuckGo and web fetch uses direct HTTP. " +
        "Install Docker Desktop later in Settings to enable server backed search and scraping.";

    private static async Task<(bool Ok, string Output)> TryRunAsync(string file, string args, CancellationToken ct)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!process.Start())
            {
                return (false, string.Empty);
            }

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode == 0, output.Trim());
        }
        catch
        {
            return (false, string.Empty);
        }
    }
}
