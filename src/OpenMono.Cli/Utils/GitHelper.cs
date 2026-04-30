namespace OpenMono.Utils;

public static class GitHelper
{
    public static async Task<string?> GetCurrentBranchAsync(string workingDir, CancellationToken ct)
    {
        var (exit, stdout, _) = await ProcessRunner.RunAsync(
            "git rev-parse --abbrev-ref HEAD", workingDir, ct: ct);
        return exit == 0 ? stdout.Trim() : null;
    }

    public static async Task<bool> IsGitRepoAsync(string workingDir, CancellationToken ct)
    {
        var (exit, _, _) = await ProcessRunner.RunAsync(
            "git rev-parse --is-inside-work-tree", workingDir, ct: ct);
        return exit == 0;
    }

    public static async Task<string?> GetRepoRootAsync(string workingDir, CancellationToken ct)
    {
        var (exit, stdout, _) = await ProcessRunner.RunAsync(
            "git rev-parse --show-toplevel", workingDir, ct: ct);
        return exit == 0 ? stdout.Trim() : null;
    }

    public static async Task<string?> GetContextAsync(string workingDir, CancellationToken ct = default)
    {
        if (!await IsGitRepoAsync(workingDir, ct))
            return null;

        var branch = await GetCurrentBranchAsync(workingDir, ct) ?? "unknown";

        var (_, statusOut, _) = await ProcessRunner.RunAsync(
            "git status --short", workingDir, ct: ct);
        var statusLines = statusOut.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var dirty = statusLines.Length > 0
            ? $"{statusLines.Length} file(s) modified/untracked"
            : "clean";

        var (_, logOut, _) = await ProcessRunner.RunAsync(
            "git log --oneline -3", workingDir, ct: ct);
        var commits = string.IsNullOrWhiteSpace(logOut) ? "(no commits)" : logOut.Trim();

        return $"Branch: {branch} | {dirty}\nRecent commits:\n{commits}";
    }
}
