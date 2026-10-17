using SkiaSharp;

namespace OpenMono.Utils;

public static class ImageUtils
{
    private const long MaxPixels = 1280L * 32 * 32;

    public static readonly HashSet<string> Extensions =
        new(StringComparer.OrdinalIgnoreCase) { "png", "jpg", "jpeg", "gif", "webp" };

    public static bool IsImage(string path) =>
        Extensions.Contains(Path.GetExtension(path).TrimStart('.'));

    public static string MimeFromExt(string ext) =>
        ext is "jpg" or "jpeg" ? "image/jpeg" : $"image/{ext}";

    public static (byte[] bytes, string mime) SmartResize(byte[] raw, string origMime)
    {
        SKBitmap? src = null;
        try
        {
            src = SKBitmap.Decode(raw);
            if (src == null)
                return (raw, origMime);

            long pixels = (long)src.Width * src.Height;
            if (pixels <= MaxPixels)
                return (raw, origMime);

            double scale = Math.Sqrt((double)MaxPixels / pixels);
            int newW = Math.Max(32, (int)(Math.Round(src.Width  * scale / 32.0) * 32));
            int newH = Math.Max(32, (int)(Math.Round(src.Height * scale / 32.0) * 32));

            using var resized = src.Resize(new SKImageInfo(newW, newH), new SKSamplingOptions(SKFilterMode.Linear));
            using var data = resized.Encode(SKEncodedImageFormat.Jpeg, 90);
            return (data.ToArray(), "image/jpeg");
        }
        catch
        {
            return (raw, origMime);
        }
        finally
        {        
            src?.Dispose();
        }
    }
}
