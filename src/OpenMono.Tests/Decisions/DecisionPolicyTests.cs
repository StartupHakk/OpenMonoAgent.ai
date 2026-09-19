using FluentAssertions;
using OpenMono.Decisions;

namespace OpenMono.Tests.Decisions;

public class DecisionPolicyTests
{
    [Theory]
    [InlineData(0.85, "write")]
    [InlineData(1.0, "write")]
    [InlineData(0.849, "review")]
    [InlineData(0.6, "review")]
    [InlineData(0.599, "escalate")]
    [InlineData(0.0, "escalate")]
    public void ApplyGate_MapsConfidenceToDestination(double confidence, string expected)
    {
        DecisionPolicy.ApplyGate("write", confidence, 0.85, 0.6).Should().Be(expected);
    }

    [Theory]
    [InlineData(0.9, "yes")]
    [InlineData(0.85, "yes")]
    [InlineData(0.1, "no")]
    [InlineData(0.15, "no")]
    [InlineData(0.7, "review")]
    [InlineData(0.3, "review")]
    [InlineData(0.5, "escalate")]
    [InlineData(0.45, "escalate")]
    public void NoulGate_MapsProbabilityToVerdict(double p, string expected)
    {
        DecisionPolicy.NoulGate(p).Should().Be(expected);
    }

    [Theory]
    [InlineData(0.9, "auto")]
    [InlineData(0.85, "auto")]
    [InlineData(0.7, "review")]
    [InlineData(0.6, "review")]
    [InlineData(0.5, "escalate")]
    public void ScoreBand_MapsScoreToBand(double score, string expected)
    {
        DecisionPolicy.ScoreBand(score, 0.85, 0.6).Should().Be(expected);
    }
}
