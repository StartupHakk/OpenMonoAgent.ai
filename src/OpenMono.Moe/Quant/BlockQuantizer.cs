namespace OpenMono.Moe.Quant;

/// <summary>
/// Block absmax quantizer used by the OMS1 packer. This is a GSQ-style
/// placeholder (uniform levels, per-block scale), not ISTA-DASLab GSQ-RCO.
/// Good enough to lock bit assignment and size math on synthetic weights.
/// </summary>
public static class BlockQuantizer
{
    public const int DefaultBlockSize = 32;

    public static PackedTensor Quantize(ReadOnlySpan<float> weights, int bits, int blockSize = DefaultBlockSize)
    {
        if (bits is < 2 or > 8)
            throw new ArgumentOutOfRangeException(nameof(bits), "BlockQuantizer supports 2 to 8 bits.");
        if (blockSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize));

        int levels = (1 << bits) - 1;
        int n = weights.Length;
        int blocks = (n + blockSize - 1) / blockSize;
        var scales = new float[blocks];
        var codes = new byte[n];

        for (int b = 0; b < blocks; b++)
        {
            int start = b * blockSize;
            int end = Math.Min(start + blockSize, n);
            float maxAbs = 0f;
            for (int i = start; i < end; i++)
                maxAbs = Math.Max(maxAbs, Math.Abs(weights[i]));

            float scale = maxAbs == 0f ? 1f : maxAbs;
            scales[b] = scale;
            float inv = levels / (2f * scale);
            for (int i = start; i < end; i++)
            {
                float q = Math.Clamp(MathF.Round(weights[i] * inv + levels / 2f), 0, levels);
                codes[i] = (byte)q;
            }
        }

        return new PackedTensor(bits, blockSize, n, codes, scales);
    }

    public static float[] Dequantize(PackedTensor packed)
    {
        var dst = new float[packed.Count];
        DequantizeTo(packed, dst);
        return dst;
    }

    public static void DequantizeTo(PackedTensor packed, Span<float> dst)
    {
        if (dst.Length < packed.Count)
            throw new ArgumentException("Destination is shorter than the packed tensor.", nameof(dst));

        int levels = (1 << packed.Bits) - 1;
        int blockSize = packed.BlockSize;
        for (int i = 0; i < packed.Count; i++)
        {
            int b = i / blockSize;
            float scale = packed.Scales[b];
            float centered = packed.Codes[i] - levels / 2f;
            dst[i] = centered * (2f * scale / levels);
        }
    }

    public static double MeanAbsError(ReadOnlySpan<float> original, ReadOnlySpan<float> reconstructed)
    {
        if (original.Length != reconstructed.Length)
            throw new ArgumentException("Length mismatch.");
        double acc = 0;
        for (int i = 0; i < original.Length; i++)
            acc += Math.Abs(original[i] - reconstructed[i]);
        return acc / original.Length;
    }

    public static long PackedBytes(int count, int bits, int blockSize)
    {
        long codeBits = (long)count * bits;
        long codeBytes = (codeBits + 7) / 8;
        int blocks = (count + blockSize - 1) / blockSize;
        return codeBytes + blocks * 2; // fp16 scales in the on-disk layout
    }
}

public sealed record PackedTensor(int Bits, int BlockSize, int Count, byte[] Codes, float[] Scales);
