using FluentAssertions;
using OpenMono.Decisions;

namespace OpenMono.Tests.Decisions;

public class ClosedWorldChecksTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(255)]
    public void CheckOptionCount_AcceptsOneTo255(int count)
    {
        ClosedWorldChecks.CheckOptionCount(count).Should().BeNull();
    }

    [Fact]
    public void CheckOptionCount_RejectsZeroAndOverflow()
    {
        ClosedWorldChecks.CheckOptionCount(0).Should().Be("menu-empty");
        ClosedWorldChecks.CheckOptionCount(256).Should().Be("menu-overflow:256");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    public void CheckLevelCount_AcceptsTwoToTen(int count)
    {
        ClosedWorldChecks.CheckLevelCount(count).Should().BeNull();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    public void CheckLevelCount_RejectsOutsideTwoToTen(int count)
    {
        ClosedWorldChecks.CheckLevelCount(count).Should().Be($"levels-out-of-range:{count}");
    }

    [Fact]
    public void CheckProbabilitiesSum_AcceptsNormalized()
    {
        var probs = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["a"] = 0.5,
            ["b"] = 0.3,
            ["c"] = 0.2,
        };

        ClosedWorldChecks.CheckProbabilitiesSum(probs).Should().BeNull();
    }

    [Fact]
    public void CheckProbabilitiesSum_RejectsUnnormalized()
    {
        var probs = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["a"] = 0.3,
            ["b"] = 0.2,
        };

        ClosedWorldChecks.CheckProbabilitiesSum(probs).Should().StartWith("probs-not-normalized:");
    }

    [Fact]
    public void CheckProbabilitiesSum_RejectsNonFiniteAndOutOfRange()
    {
        var nan = new Dictionary<string, double>(StringComparer.Ordinal) { ["a"] = double.NaN };
        ClosedWorldChecks.CheckProbabilitiesSum(nan).Should().Be("non-finite-prob");

        var big = new Dictionary<string, double>(StringComparer.Ordinal) { ["a"] = 1.5 };
        ClosedWorldChecks.CheckProbabilitiesSum(big).Should().Be("prob-out-of-range");
    }

    [Fact]
    public void CheckChoiceIsArgmax_AcceptsMaxKey()
    {
        var probs = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["a"] = 0.2,
            ["b"] = 0.8,
        };

        ClosedWorldChecks.CheckChoiceIsArgmax("b", probs).Should().BeNull();
    }

    [Fact]
    public void CheckChoiceIsArgmax_RejectsNonMax()
    {
        var probs = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["a"] = 0.2,
            ["b"] = 0.8,
        };

        ClosedWorldChecks.CheckChoiceIsArgmax("a", probs).Should().Be("choice-not-argmax:a");
    }

    [Fact]
    public void CheckChoiceIsArgmax_TieGoesToFirst()
    {
        var probs = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["a"] = 0.5,
            ["b"] = 0.5,
        };

        ClosedWorldChecks.CheckChoiceIsArgmax("a", probs).Should().BeNull();
        ClosedWorldChecks.CheckChoiceIsArgmax("b", probs).Should().Be("choice-not-argmax:b");
    }

    [Fact]
    public void CheckNonEmpty_RejectsBlank()
    {
        ClosedWorldChecks.CheckNonEmpty("Research goals").Should().BeNull();
        ClosedWorldChecks.CheckNonEmpty("   ").Should().Be("empty-value");
    }
}
