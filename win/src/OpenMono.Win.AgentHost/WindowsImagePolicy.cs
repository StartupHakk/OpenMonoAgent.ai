using OpenMono.Win.Foundry;

namespace OpenMono.Win.AgentHost;

// Never-drop-images policy for Windows chat. OMA already attaches images as
// ImagePart data URLs and sends them as image_url parts on
// /v1/chat/completions, in the Linux agent container, unchanged. This policy
// only decides which model name an image turn carries (via OPENMONO_MODEL
// into the container or the /model command) and raises a clear, actionable
// error when none can, instead of sending a text-only request that silently
// ignores the attachments.
public static class WindowsImagePolicy
{
    public static VisionRouteResult RouteImageTurn(
        bool hasImages,
        string targetModel,
        VisionProbeResult? targetProbe,
        string? resolvedVisionFallback,
        string visionFallbackAlias = FoundryConstants.DefaultVisionFallbackAlias)
    {
        return FoundryVisionRouter.SelectModelForTurn(
            hasImages, targetModel, visionFallbackAlias, targetProbe, resolvedVisionFallback);
    }

    // Throws with user-facing remediation when images cannot be served.
    // Callers surface ex.Message directly. It follows the same wording as the
    // upstream ConversationLoop warning (inference.vision_enabled key plus a
    // server-side step), with the Windows server-side step spelled out.
    public static string RequireVisionModel(VisionRouteResult route, string visionFallbackAlias)
    {
        if (route.ImagesWillBeSent && route.ModelAlias is not null)
        {
            return route.ModelAlias;
        }

        throw new InvalidOperationException(
            "Image attachments cannot be served: " + route.Reason + " "
            + $"Serve a vision-capable model first (`foundry model load {visionFallbackAlias}` or another "
            + "Foundry vision catalog model), then set inference.vision_enabled=true "
            + "(`openmono config set inference.vision_enabled true`) and retry with the image attached.");
    }
}
