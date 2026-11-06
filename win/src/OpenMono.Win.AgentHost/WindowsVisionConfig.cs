namespace OpenMono.Win.AgentHost;

// Maps the host llama-server's image support onto the single product-wide
// vision flag. Upstream resolution (AppConfig.ResolveVision + Program.cs props
// handling): explicit inference.vision_enabled wins, otherwise the server's
// reported capability decides. On Windows, as on Linux, llama-server takes
// images exactly when it was started with the model's --mmproj projector, so
// the launcher knows the answer without a probe. An explicit user setting is
// never overridden.
public static class WindowsVisionConfig
{
    public const string VisionEnvVar = "OPENMONO_VISION_ENABLED";

    public sealed record VisionResolution(bool VisionEnabled, string Reason);

    public static VisionResolution Resolve(bool? explicitSetting, bool mmprojLoaded, string model)
    {
        if (explicitSetting.HasValue)
        {
            return new VisionResolution(
                explicitSetting.Value,
                "Explicit inference.vision_enabled="
                    + (explicitSetting.Value ? "true" : "false")
                    + " respected.");
        }

        return mmprojLoaded
            ? new VisionResolution(true, $"llama-server loaded the vision projector for {model}; vision enabled.")
            : new VisionResolution(false,
                $"Vision off: llama-server runs {model} without its vision projector (mmproj). "
                    + "Run .\\win\\openmono.ps1 setup to download it, then start again.");
    }
}
