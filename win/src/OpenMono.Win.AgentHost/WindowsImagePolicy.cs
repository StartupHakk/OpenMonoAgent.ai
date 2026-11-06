using OpenMono.Win.Foundry;

namespace OpenMono.Win.AgentHost;

// Never-drop-images policy for Windows chat. OMA already attaches images as
// ImagePart data URLs and sends them as image_url parts on
// /v1/chat/completions, in the Linux agent container, unchanged. This policy
// only decides whether an image turn can go to the one served model, and
// raises a clear, actionable error when it cannot, instead of sending a
// text-only request that silently ignores the attachments.
public static class WindowsImagePolicy
{
    public static VisionRouteResult RouteImageTurn(
        bool hasImages,
        string servedModel,
        VisionProbeResult? servedProbe) =>
        FoundryVisionRouter.SelectModelForTurn(hasImages, servedModel, servedProbe);

    // Throws with user-facing remediation when images cannot be served.
    // Callers surface ex.Message directly. It follows the same wording as the
    // upstream ConversationLoop warning (inference.vision_enabled key plus a
    // server-side step), with the Windows server-side step spelled out.
    public static string RequireVisionModel(VisionRouteResult route)
    {
        if (route.ImagesWillBeSent && route.ModelAlias is not null)
        {
            return route.ModelAlias;
        }

        throw new InvalidOperationException(
            "Image attachments cannot be served: " + route.Reason + " "
            + "Below 24 GB the Qwen3.5 models OpenMono serves take images; on 24 GB+ cards it serves only "
            + FoundryConstants.TargetModelAlias + ", whose Foundry build may be text-only. "
            + "Once the served model accepts images, set inference.vision_enabled=true "
            + "(`openmono config set inference.vision_enabled true`) and retry with the image attached.");
    }
}
