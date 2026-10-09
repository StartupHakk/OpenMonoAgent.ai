using FluentAssertions;
using OpenMono.Utils;
using SkiaSharp;

namespace OpenMono.Tests.Utils;

public class ImageUtilsTests
{
    // ~1.3 MP budget: 1280 * 32 * 32
    private const long MaxPixels = 1280L * 32 * 32;

    // Generates a real, non-trivial test image using core SKBitmap APIs.
    private static byte[] MakeImage(int w, int h, SKEncodedImageFormat fmt)
    {
        using var bmp = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                bmp.SetPixel(x, y, new SKColor((byte)(x % 256), (byte)(y % 256), (byte)((x + y) % 256)));
        using var data = bmp.Encode(fmt, 90);
        return data.ToArray();
    }

    private static (int w, int h) DecodeDims(byte[] bytes)
    {
        using var bmp = SKBitmap.Decode(bytes)!;
        return (bmp.Width, bmp.Height);
    }

    [Fact]
    public void SmartResize_SmallImage_PassesThroughUnchanged()
    {
        var raw = MakeImage(200, 200, SKEncodedImageFormat.Jpeg);
        var (outBytes, mime) = ImageUtils.SmartResize(raw, "image/jpeg");

        Assert.Same(raw, outBytes); // under the pixel budget: returned untouched
        Assert.Equal("image/jpeg", mime);
    }

    [Fact]
    public void SmartResize_LargeImage_DownscalesWithinBudgetAndAlignsTo32()
    {
        // 1920x1080 = 2.07 MP > 1.3 MP budget, so it must be resized.
        var raw = MakeImage(1920, 1080, SKEncodedImageFormat.Jpeg);
        var (outBytes, mime) = ImageUtils.SmartResize(raw, "image/jpeg");

        Assert.Equal("image/jpeg", mime);
        Assert.NotSame(raw, outBytes); // oversized image must be re-encoded
        Assert.NotEmpty(outBytes);

        var (w, h) = DecodeDims(outBytes);
        // Rounding each dim to a multiple of 32 can overshoot the budget by one
        // 32px block per dimension; allow up to one block per axis.
        long maxAllowed = (long)(w + 32) * (h + 32);
        Assert.True((long)w * h <= maxAllowed, $"resized image far over budget, got {w}x{h}");
        Assert.Equal(0, w % 32); // width multiple of 32
        Assert.True(w >= 32);
        Assert.Equal(0, h % 32); // height multiple of 32
        Assert.True(h >= 32);

        // Aspect ratio roughly preserved (within rounding tolerance).
        double originalRatio = 1920.0 / 1080.0;
        double newRatio = w / (double)h;
        Assert.True(Math.Abs(newRatio - originalRatio) <= 0.05,
            $"aspect ratio drifted: {newRatio:F4} vs {originalRatio:F4}");
    }

    [Fact]
    public void SmartResize_PngInput_ReturnsJpeg()
    {
        // 1600x1200 = 1.92 MP > budget, PNG in -> JPEG out.
        var raw = MakeImage(1600, 1200, SKEncodedImageFormat.Png);
        var (outBytes, mime) = ImageUtils.SmartResize(raw, "image/png");

        Assert.Equal("image/jpeg", mime);
        Assert.NotEmpty(outBytes);
        var (w, h) = DecodeDims(outBytes);
        long maxAllowed = (long)(w + 32) * (h + 32);
        Assert.True((long)w * h <= maxAllowed, $"resized image far over budget, got {w}x{h}");
    }

    [Fact]
    public void SmartResize_InvalidBytes_ReturnsOriginalUnchanged()
    {
        var raw = new byte[] { 0x00, 0x01, 0x02, 0x03 }; // not a real image
        var (outBytes, mime) = ImageUtils.SmartResize(raw, "image/png");

        Assert.Same(raw, outBytes); // undecodable bytes pass through untouched
        Assert.Equal("image/png", mime);
    }

    [Fact]
    public void IsImage_MatchesSupportedExtensions()
    {
        Assert.True(ImageUtils.IsImage("a/b/c.png"));
        Assert.True(ImageUtils.IsImage("photo.JPEG"));
        Assert.True(ImageUtils.IsImage("anim.gif"));
        Assert.True(ImageUtils.IsImage("pic.webp"));
        Assert.False(ImageUtils.IsImage("doc.pdf"));
        Assert.False(ImageUtils.IsImage("noext"));
    }
}
