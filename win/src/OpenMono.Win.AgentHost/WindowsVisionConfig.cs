using OpenMono.Win.Foundry;

namespace OpenMono.Win.AgentHost;

// Maps the Foundry vision probe onto the single product-wide vision flag.
// Upstream resolution (AppConfig.ResolveVision + Program.cs props handling):
// explicit inference.vision_enabled wins, otherwise the server's reported
// capability decides. The only Windows difference is how the capability is
// learned: llama.cpp reports modalities.vision in /props, while Foundry is
// asked directly with a 1x1 image probe (FoundryVisionProbe). An explicit
// user setting is never overridden by either probe.
public static class WindowsVisionConfig
{
    public const string VisionEnvVar = "OPENMONO_VISION_ENABLED";

    public sealed record VisionResolution(bool VisionEnabled, string Reason);

    public static VisionResolution ApplyProbeResult(
        bool? explicitSetting,
        VisionProbeResult? probe)
    {
        if (explicitSetting.HasValue)
        {
            return new VisionResolution(
                explicitSetting.Value,
                "Explicit inference.vision_enabled="
                    + (explicitSetting.Value ? "true" : "false")
                    + " respected; probe result ignored.");
        }

        if (probe?.Accepted == true)
        {
            return new VisionResolution(
                true,
                "Foundry endpoint accepted image input for " + probe.Model + "; vision enabled.");
        }

        return new VisionResolution(
            false,
            "Vision off: "
                + (probe?.Detail ?? "no vision probe ran")
                + " Set inference.vision_enabled=true to override once a vision model is serving.");
    }

    // Mirrors what the container launcher does with the resolved flag: export
    // it so every downstream consumer (ConfigLoader, ConversationLoop warning)
    // sees the same value. Never called when the user set the key explicitly.
    public static void ExportResolved(bool visionEnabled) =>
        Environment.SetEnvironmentVariable(VisionEnvVar, visionEnabled ? "1" : "0");
}
