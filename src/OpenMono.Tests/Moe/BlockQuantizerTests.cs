using FluentAssertions;
using OpenMono.Moe.Quant;

namespace OpenMono.Tests.Moe;

public class BlockQuantizerTests
{
    [Fact]
    public void Quantize_Dequantize_StaysCloseOnSmoothWeights()
    {
        var src = Enumerable.Range(0, 64).Select(i => MathF.Sin(i / 7f)).ToArray();
        var packed = BlockQuantizer.Quantize(src, bits: 3);
        var back = BlockQuantizer.Dequantize(packed);
        BlockQuantizer.MeanAbsError(src, back).Should().BeLessThan(0.15);
        packed.Bits.Should().Be(3);
        packed.Count.Should().Be(64);
    }

    [Fact]
    public void TwoBit_IsSmallerThanThreeBit_OnDiskEstimate()
    {
        long two = BlockQuantizer.PackedBytes(4096, 2, 32);
        long three = BlockQuantizer.PackedBytes(4096, 3, 32);
        two.Should().BeLessThan(three);
        two.Should().Be(4096 * 2 / 8 + 128 * 2); // 1024 codes + 128 fp16 scales
    }

    [Fact]
    public void Rejects_IllegalBitWidth()
    {
        var act = () => BlockQuantizer.Quantize([1f, 2f], bits: 1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
