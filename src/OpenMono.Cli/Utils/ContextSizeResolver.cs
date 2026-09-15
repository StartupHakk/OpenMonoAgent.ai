namespace OpenMono.Utils;

public static class ContextSizeResolver
{
    public const int MinimumPlausibleContextSize = 1024;

    public static int ResolveContextSize(int? serverCtx, int configuredCtx)
        => serverCtx is >= MinimumPlausibleContextSize ? serverCtx.Value : configuredCtx;
}
