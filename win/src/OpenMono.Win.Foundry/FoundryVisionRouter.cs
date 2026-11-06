namespace OpenMono.Win.Foundry;

// Picks which model serves a turn once image attachments are in play.
// Never silently drops images: when nothing can serve them the router says
// so explicitly and the host must surface that to the user instead of
// sending a text-only request.
public sealed record VisionRouteResult(
    string? ModelAlias,
    bool ImagesWillBeSent,
    string Reason);

public static class FoundryVisionRouter
{
    public static VisionRouteResult SelectModelForTurn(
        bool hasImages,
        string targetModel,
        string visionFallbackModel,
        VisionProbeResult? targetProbe,
        string? resolvedVisionFallback)
    {
        if (!hasImages)
        {
            return new VisionRouteResult(
                targetModel, false, "Text-only turn; serving from " + targetModel + ".");
        }

        if (targetProbe?.Accepted == true)
        {
            return new VisionRouteResult(
                targetModel, true,
                targetModel + " accepted image input at startup probe; sending images with the turn.");
        }

        if (resolvedVisionFallback is not null)
        {
            var why = targetProbe is null
                ? "target model was not vision-probed"
                : "target model refused image input (" + (targetProbe.Detail) + ")";
            return new VisionRouteResult(
                resolvedVisionFallback, true,
                "Image turn routed to " + resolvedVisionFallback + " because " + why + ".");
        }

        return new VisionRouteResult(
            null, false,
            "This turn has image attachments but no vision-capable model is available: "
            + (targetProbe?.Detail ?? "no vision probe ran")
            + ". Load a Foundry vision catalog model first; the images were NOT sent.");
    }
}
