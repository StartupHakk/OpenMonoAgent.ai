using FluentAssertions;
using OpenMono.Moe.Family;
using OpenMono.Moe.Forward;
using OpenMono.Moe.Runtime;

namespace OpenMono.Tests.Moe;

public class RouterAndForwardTests
{
    [Fact]
    public void Router_PicksTopK_AndNormalizes()
    {
        var shape = new MimoV26FlashAdapter().Shape with
        {
            HiddenSize = 4,
            NRoutedExperts = 4,
            NumExpertsPerTok = 2,
        };
        // One-hot-ish rows so expert 2 then 0 win.
        var w = new float[4 * 4];
        w[2 * 4 + 0] = 8f;
        w[0 * 4 + 0] = 4f;
        w[1 * 4 + 0] = 1f;
        var hidden = new float[] { 1, 0, 0, 0 };
        var decision = ExpertRouter.Route(hidden, w, shape);
        decision.ExpertIds.Should().Equal(2, 0);
        decision.Weights.Sum().Should().BeApproximately(1f, 1e-5f);
        decision.Weights[0].Should().BeGreaterThan(decision.Weights[1]);
    }

    [Fact]
    public void ToyForward_IsDeterministic_AndRoutesTwoExpertsOnMoeLayer()
    {
        var a = new ToyMimoCpuForward(seed: 11);
        var b = new ToyMimoCpuForward(seed: 11);
        int[] tokens = [3, 5, 8, 1];
        var ra = a.Forward(tokens);
        var rb = b.Forward(tokens);
        ra.Logits.Should().Equal(rb.Logits);
        ra.Logits.Length.Should().Be(tokens.Length * ToyMimoCpuForward.VocabSize);
        ra.RoutedExpertsPerToken.Should().HaveCount(tokens.Length);
        foreach (var ids in ra.RoutedExpertsPerToken)
        {
            ids.Should().HaveCount(ToyMimoCpuForward.TopK);
            ids.Should().OnlyHaveUniqueItems();
            ids.Should().OnlyContain(id => id >= 0 && id < ToyMimoCpuForward.NExperts);
        }
    }

    [Fact]
    public void ToyForward_DifferentSeeds_Differ()
    {
        var left = new ToyMimoCpuForward(1).Forward([4, 9]);
        var right = new ToyMimoCpuForward(2).Forward([4, 9]);
        left.Logits.Should().NotEqual(right.Logits);
    }

    [Fact]
    public void Registry_OwnsMimo_AndLeavesQwenToStrata()
    {
        var reg = new AdapterRegistry();
        reg.RequireOwned(MimoV26FlashAdapter.FamilyId).OwnsRuntime.Should().BeTrue();
        var qwen = reg.Resolve(Qwen38FlashNextAdapter.FamilyId);
        qwen.Should().NotBeNull();
        qwen!.OwnsRuntime.Should().BeFalse();
        qwen.RuntimeOwner.Should().Be("strata");
        var act = () => reg.RequireOwned(Qwen38FlashNextAdapter.FamilyId);
        act.Should().Throw<NotSupportedException>().WithMessage("*owned by strata*");
    }

    [Fact]
    public void OfficialMimoShape_MatchesPublishedCard()
    {
        var shape = new MimoV26FlashAdapter().Shape;
        shape.NumLayers.Should().Be(48);
        shape.GaLayers.Should().Be(9);
        shape.SwaLayers.Should().Be(39);
        shape.MoeLayers.Should().Be(47);
        shape.DenseLayers.Should().Be(1);
        shape.NRoutedExperts.Should().Be(256);
        shape.NumExpertsPerTok.Should().Be(8);
        shape.SlidingWindow.Should().Be(128);
        shape.MaxContext.Should().Be(1_048_576);
        shape.IsMoeLayer(0).Should().BeFalse();
        shape.IsGlobalAttention(0).Should().BeTrue();
        shape.MtpDraftTokens.Should().Be(7);
    }

    [Fact]
    public void RecommendedConfig_FitsConsumerBox()
    {
        var report = MemoryBudget.Evaluate(MosaicRecommendedConfig.MimoFlashRl);
        report.Fits.Should().BeTrue(
            $"A should fit: VRAM {report.VramUsedGiB:F2}/{report.VramLimitGiB} RAM {report.RamUsedGiB:F2}/{report.RamLimitGiB}");
        report.RamUsedGiB.Should().BeLessThanOrEqualTo(MemoryBudget.ConsumerRamGiB);
        MosaicRecommendedConfig.MimoFlashRl.Context.Should().Be(131_072);
        MosaicRecommendedConfig.MimoFlashRl.ExpertsKept.Should().Be(128);
    }

    [Fact]
    public void UnprunedIq2_DoesNotFit_CombinedEnvelope()
    {
        // TrevorJS IQ2 91.90 GiB + 2 GiB DFlash, even with a generous GPU/RAM split.
        var tooBig = MosaicRecommendedConfig.MimoFlashRl with
        {
            Id = "unpruned-iq2",
            WeightVramGiB = 18,
            WeightRamGiB = 73.90,
            MtpRamGiB = 2.08,
            MtpVramGiB = 0,
        };
        MemoryBudget.Evaluate(tooBig).Fits.Should().BeFalse();
    }
}
