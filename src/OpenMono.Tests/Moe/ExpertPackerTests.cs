using FluentAssertions;
using OpenMono.Moe.Family;
using OpenMono.Moe.Quant;

namespace OpenMono.Tests.Moe;

public class ExpertPackerTests
{
    [Fact]
    public void Pack_RoundTrips_SyntheticExpert()
    {
        var adapter = new MimoV26FlashAdapter();
        var weights = Enumerable.Range(0, 32).Select(i => i * 0.01f - 0.1f).ToArray();
        var blob = ExpertPacker.Pack(adapter, layer: 47, expert: 3, ExpertRole.Down, weights, rows: 8, cols: 4);
        blob[..4].Should().Equal("OMS1"u8.ToArray());

        var packed = ExpertPacker.Unpack(blob);
        packed.Family.Should().Be(MosaicFamily.MimoV26Flash);
        packed.Layer.Should().Be(47);
        packed.Expert.Should().Be(3);
        packed.Role.Should().Be(ExpertRole.Down);
        packed.BpwTenths.Should().Be(33, "last-layer down-proj is the high-bit role");
        packed.Rows.Should().Be(8);
        packed.Cols.Should().Be(4);
        packed.Tensor.Count.Should().Be(32);
    }

    [Fact]
    public void RoleAwareBits_AssignsMoreToDownAndLastLayer()
    {
        var adapter = new MimoV26FlashAdapter();
        adapter.BitsPerWeightTenths(ExpertRole.Gate, layer: 10).Should().Be(20);
        adapter.BitsPerWeightTenths(ExpertRole.Down, layer: 10).Should().Be(33);
        adapter.BitsPerWeightTenths(ExpertRole.Gate, layer: 47).Should().Be(33);
    }

    [Fact]
    public void Pack_Refuses_QwenFamilyOwnedByStrata()
    {
        var qwen = new Qwen38FlashNextAdapter();
        var act = () => ExpertPacker.Pack(qwen, 0, 0, ExpertRole.Gate, [1f], 1, 1);
        act.Should().Throw<NotSupportedException>().WithMessage("*Strata*");
    }
}
