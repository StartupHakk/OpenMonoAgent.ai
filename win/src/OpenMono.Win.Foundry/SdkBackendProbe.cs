using System.Reflection;

namespace OpenMono.Win.Foundry;

// M1 stub for the optional C# SDK path. The host prefers the CLI until the
// Microsoft.AI.Foundry.Local package coordinate is confirmed (see csproj and
// BUILD-PLAN.md). This probe only reports whether the SDK assembly is present
// so a future typed client can activate without changing callers.
public sealed record SdkProbeResult(bool Available, string? Version, string Detail);

public static class SdkBackendProbe
{
    public static SdkProbeResult Probe()
    {
        try
        {
            var assembly = Assembly.Load(FoundryConstants.SdkAssemblyName);
            var version = assembly.GetName().Version?.ToString();
            return new SdkProbeResult(
                true,
                version,
                $"SDK assembly {FoundryConstants.SdkAssemblyName} present (version {version ?? "unknown"}). " +
                "Typed SDK client not yet implemented; CLI path remains active.");
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return new SdkProbeResult(
                false,
                null,
                $"SDK assembly {FoundryConstants.SdkAssemblyName} not present ({ex.GetType().Name}). Using the CLI path.");
        }
    }
}
