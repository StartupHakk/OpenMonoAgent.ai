using FluentAssertions;
using OpenMono.Decisions;

namespace OpenMono.Tests.Decisions;

public class HeuristicBackendTests
{
    private readonly HeuristicBackend _backend =
        new(new DecisionOptions(false, 0.85, 0.6, 0.5, 64));

    [Fact]
    public void Choose_PicksResearchWhenEvidenceIsMissing()
    {
        var options = new Dictionary<string, string?>
        {
            ["research"] = "Collect evidence still needed.",
            ["write"] = "Draft from sufficient evidence.",
            ["review"] = "Goal unclear, out of scope, or complete.",
        };

        var (choice, _, _) = _backend.Choose("Compare three tools. No sources, no findings yet.", options);

        choice.Should().Be("research");
    }

    [Fact]
    public void Choose_PicksWriteWhenEvidenceIsPresent()
    {
        var options = new Dictionary<string, string?>
        {
            ["research"] = "Collect evidence still needed.",
            ["write"] = "Draft from sufficient evidence.",
            ["review"] = "Goal unclear, out of scope, or complete.",
        };

        var (choice, _, _) = _backend.Choose(
            "Compare three tools. Evidence collected from docs, findings drafted, sufficient to write.",
            options);

        choice.Should().Be("write");
    }

    [Fact]
    public void Choose_ReturnsFirstOptionWithUniformConfidenceOnEmptyState()
    {
        var options = new Dictionary<string, string?> { ["a"] = "First", ["b"] = "Second", ["c"] = "Third" };

        var (choice, confidence, probs) = _backend.Choose(string.Empty, options);

        choice.Should().Be("a");
        confidence.Should().BeApproximately(1.0 / 3, 1e-9);
        probs.Values.Sum().Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Choose_IsDeterministic()
    {
        var options = new Dictionary<string, string?> { ["x"] = "Fix bug", ["y"] = "Write docs" };

        var first = _backend.Choose("Fix the login bug today", options);
        var second = _backend.Choose("Fix the login bug today", options);

        second.Should().BeEquivalentTo(first);
    }

    [Fact]
    public void JudgeTrue_ScoresMatchingPropositionHigh()
    {
        _backend.JudgeTrue("Server is down, payouts failing for hours", "payouts are failing")
            .Should().BeGreaterThan(0.5);
    }

    [Fact]
    public void JudgeTrue_ScoresUnrelatedPropositionLow()
    {
        _backend.JudgeTrue("Server is down, payouts failing for hours", "documentation needs reprint")
            .Should().BeLessThan(0.5);
    }

    [Fact]
    public void Relevance_ScoresExactMatchOne()
    {
        _backend.Relevance("Phone number", "phone number").Should().Be(1.0);
    }

    [Fact]
    public void Relevance_ScoresContainmentAboveThreshold()
    {
        _backend.Relevance("phone", "Phone number field").Should().BeGreaterThanOrEqualTo(0.75);
    }

    [Fact]
    public void Relevance_ScoresUnrelatedPairLow()
    {
        _backend.Relevance("phone", "database migration checklist").Should().BeLessThan(0.5);
    }

    [Fact]
    public void Verify_MarksCoveredClaimSupported()
    {
        var (verdict, _) = _backend.Verify(
            "The database migration completed successfully on all three nodes.",
            "migration completed");

        verdict.Should().Be("supported");
    }

    [Fact]
    public void Verify_MarksAbsentClaimNotAddressed()
    {
        var (verdict, _) = _backend.Verify(
            "The database migration completed successfully on all three nodes.",
            "lunar landing schedule");

        verdict.Should().Be("not_addressed");
    }

    [Fact]
    public void Verify_MarksNegatedPartialClaimContradicted()
    {
        var (verdict, _) = _backend.Verify(
            "The deploy did not finish and the migration was not applied.",
            "migration applied cleanly everywhere today");

        verdict.Should().Be("contradicted");
    }
}
