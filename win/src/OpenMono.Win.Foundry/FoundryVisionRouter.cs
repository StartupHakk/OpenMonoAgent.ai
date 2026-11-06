namespace OpenMono.Win.Foundry;

// Decides whether a turn's image attachments can be sent. Each machine serves
// one model (FoundryModelLadder), so there is nothing to route to: the served
// model either accepted images at the startup probe or the turn's images are
// refused out loud. Never a silent text-only request, never a second model.
public sealed record VisionRouteResult(
    string? ModelAlias,
    bool ImagesWillBeSent,
    string Reason);

public static class FoundryVisionRouter
{
    public static VisionRouteResult SelectModelForTurn(
        bool hasImages,
        string servedModel,
        VisionProbeResult? servedProbe)
    {
        if (!hasImages)
        {
            return new VisionRouteResult(
                servedModel, false, "Text-only turn; serving from " + servedModel + ".");
        }

        if (servedProbe?.Accepted == true)
        {
            return new VisionRouteResult(
                servedModel, true,
                servedModel + " accepted image input at startup probe; sending images with the turn.");
        }

        return new VisionRouteResult(
            null, false,
            "This turn has image attachments but " + servedModel + " does not accept image input here: "
            + (servedProbe?.Detail ?? "no vision probe ran")
            + ". OpenMono serves one model per machine and does not load another for images; the images were NOT sent.");
    }
}
