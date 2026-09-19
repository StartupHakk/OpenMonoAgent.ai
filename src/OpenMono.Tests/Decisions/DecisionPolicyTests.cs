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

    [Theory]
    [InlineData("write", 0.7, 0.7, 0.5, "write")]
    [InlineData("write", 0.69, 0.7, 0.5, "review")]
    [InlineData("write", 0.49, 0.7, 0.5, "escalate")]
    public void ApplyGate_DelegatesToTypedGate(string choice, double confidence, double auto, double review, string expected)
    {
        DecisionPolicy.ApplyGate(choice, confidence, auto, review).Should().Be(expected);
    }

    [Theory]
    [InlineData(0.6, 0.6, 0.5, "yes")]
    [InlineData(0.4, 0.6, 0.5, "no")]
    [InlineData(0.55, 0.6, 0.5, "review")]
    [InlineData(0.5, 0.9, 0.8, "escalate")]
    public void NoulGate_DelegatesToTypedGate(double p, double auto, double review, string expected)
    {
        DecisionPolicy.NoulGate(p, auto, review).Should().Be(expected);
    }
}
