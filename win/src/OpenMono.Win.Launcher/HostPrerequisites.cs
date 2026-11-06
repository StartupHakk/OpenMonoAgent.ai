using System.Text.RegularExpressions;

namespace OpenMono.Win.Launcher;

// Git and the .NET SDK on the Windows host. setup installs them
// (win/scripts/prereqs.ps1); doctor reports them with the same rules.
public static partial class HostPrerequisites
{
    // win/global.json: sdk.version 10.0.403, rollForward latestMinor (a unit
    // test keeps the two in step).
    public static readonly Version MinDotnetSdk = new(10, 0, 403);

    public static readonly Version MinGit = new(2, 0, 0);

    public const string GitWingetId = "Git.Git";

    public static string DotnetWingetId => $"Microsoft.DotNet.SDK.{MinDotnetSdk.Major}";

    public static Version? ParseGitVersion(string output)
    {
        var m = GitVersionPattern().Match(output);
        return m.Success ? new Version(m.Value) : null;
    }

    // `dotnet --list-sdks`: "10.0.403 [C:\Program Files\dotnet\sdk]" per line.
    public static IReadOnlyList<Version> ParseSdkList(string output) =>
        output.Split('\n')
            .Select(line => SdkLinePattern().Match(line))
            .Where(m => m.Success)
            .Select(m => new Version(m.Groups[1].Value))
            .ToList();

    public static Version? UsableSdk(IEnumerable<Version> sdks) =>
        sdks.Where(v => v.Major == MinDotnetSdk.Major && v >= MinDotnetSdk).OrderDescending().FirstOrDefault();

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex GitVersionPattern();

    [GeneratedRegex(@"^\s*(\d+\.\d+\.\d+)")]
    private static partial Regex SdkLinePattern();
}
