using System.Text.RegularExpressions;

namespace OpenMono.Win.Foundry;

// Parses `foundry --version` output and gates on the minimum CLI version.
// Output spelling varies by release, so we extract the first semver triple
// instead of matching a fixed prefix.
public static partial class FoundryVersion
{
    public static bool TryParse(string? output, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        var match = VersionPattern().Match(output);
        if (!match.Success)
        {
            return false;
        }

        return Version.TryParse(match.Value, out version!);
    }

    public static bool IsSupported(Version version) => version >= FoundryConstants.MinCliVersion;

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionPattern();
}
