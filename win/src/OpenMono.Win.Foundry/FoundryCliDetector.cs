using System.Text.RegularExpressions;

namespace OpenMono.Win.Foundry;

public sealed record FoundryCliStatus(
    bool Found,
    string? RawOutput,
    Version? Version,
    bool IsSupported)
{
    public static FoundryCliStatus NotFound(string? rawOutput = null) =>
        new(false, rawOutput, null, false);
}

// Detects the Foundry Local CLI and reads its server status.
// All parsing is lenient: CLI output spelling changes between releases.
public sealed partial class FoundryCliDetector
{
    private readonly ICliRunner _runner;

    public FoundryCliDetector(ICliRunner runner)
    {
        _runner = runner;
    }

    public static string CliExecutableName =>
        OperatingSystem.IsWindows() ? FoundryConstants.CliExecutableWindows : FoundryConstants.CliExecutableUnix;

    public async Task<FoundryCliStatus> DetectAsync(CancellationToken ct = default)
    {
        CliResult result;
        try
        {
            result = await _runner.RunAsync(CliExecutableName, "--version", ct);
        }
        catch (Exception ex)
        {
            return FoundryCliStatus.NotFound(ex.Message);
        }

        var combined = (result.StandardOutput + "\n" + result.StandardError).Trim();
        if (!result.Succeeded || string.IsNullOrWhiteSpace(combined))
        {
            return FoundryCliStatus.NotFound(combined);
        }

        if (!FoundryVersion.TryParse(combined, out var version))
        {
            return new FoundryCliStatus(true, combined, null, false);
        }

        return new FoundryCliStatus(true, combined, version, FoundryVersion.IsSupported(version));
    }

    // Reads `foundry server status` and extracts the first http(s) URL, which is
    // the endpoint the agent should adopt when the user already serves a model.
    public async Task<FoundryServerStatus> GetServerStatusAsync(CancellationToken ct = default)
    {
        CliResult result;
        try
        {
            result = await _runner.RunAsync(CliExecutableName, "server status", ct);
        }
        catch (Exception ex)
        {
            return new FoundryServerStatus(false, null, ex.Message);
        }

        if (!result.Succeeded)
        {
            return new FoundryServerStatus(false, null, result.StandardError.Trim());
        }

        var combined = result.StandardOutput + "\n" + result.StandardError;
        var match = ServerUrlPattern().Match(combined);
        if (!match.Success)
        {
            return new FoundryServerStatus(false, null, combined.Trim());
        }

        // Status lines may point at a sub-path (older builds printed
        // ".../openai/status"); the OpenAI-compatible root is the authority.
        var url = Uri.TryCreate(match.Value.TrimEnd('/', '.', ','), UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : match.Value.TrimEnd('/');

        return new FoundryServerStatus(true, url, combined.Trim());
    }

    [GeneratedRegex(@"https?://[^\s""']+")]
    private static partial Regex ServerUrlPattern();
}

public sealed record FoundryServerStatus(bool Running, string? Url, string? Detail);
