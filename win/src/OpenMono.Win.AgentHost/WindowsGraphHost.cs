using OpenMono.Win.Foundry;

namespace OpenMono.Win.AgentHost;

// Graph technology on Windows, reusing the same tools as Mac/Linux:
// code-review-graph (auto-registered as the code-graph MCP server when its
// database exists) and the graphify CLI (queried through a shell). The only
// Windows-specific work is executable resolution (.exe/.cmd on PATH) and
// routing shell invocations through the Windows shell selector instead of
// /bin/bash. No graph logic is reimplemented here.
public sealed record GraphToolStatus(bool Available, string? Command, string? Version);

public static class WindowsGraphHost
{
    private static readonly string[] CodeReviewGraphCandidates =
        ["code-review-graph.exe", "code-review-graph.cmd", "code-review-graph"];

    // Probes each candidate with `--version` and returns the first that runs.
    public static async Task<GraphToolStatus> ProbeCodeReviewGraphAsync(
        ICliRunner runner,
        CancellationToken ct = default)
    {
        foreach (var candidate in CodeReviewGraphCandidates)
        {
            CliResult result;
            try
            {
                result = await runner.RunAsync(candidate, "--version", ct);
            }
            catch (Exception)
            {
                continue;
            }

            if (!result.Succeeded)
            {
                continue;
            }

            var version = FoundryVersion.TryParse(
                result.StandardOutput + "\n" + result.StandardError, out var parsed)
                ? parsed.ToString()
                : result.StandardOutput.Trim();
            return new GraphToolStatus(true, candidate, version);
        }

        return new GraphToolStatus(
            false, null, "code-review-graph not found on PATH; graph MCP tools stay disabled.");
    }

    // graphify runs through the Windows shell (upstream docs invoke it via
    // Bash). Returns the shell and full argument line for the caller to spawn.
    public static (string Shell, string Args) GraphifyInvocation(
        string graphifyArgs,
        Func<string, bool> exists)
    {
        var shell = WindowsShellSelector.ResolveAvailable(exists);
        return (shell, "graphify " + graphifyArgs);
    }
}
