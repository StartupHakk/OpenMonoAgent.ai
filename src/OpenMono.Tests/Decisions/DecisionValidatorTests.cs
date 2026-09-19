using FluentAssertions;
using OpenMono.Decisions;

namespace OpenMono.Tests.Decisions;

public class DecisionValidatorTests
{
    private static readonly HashSet<string> Vocab =
        new(StringComparer.Ordinal) { "act", "verify", "done", "skip" };

    [Fact]
    public void Validate_AcceptsWellFormedDecisions()
    {
        var items = new[]
        {
            new DecisionItem("file", "a.cs", "act", 0.9, 0, null),
            new DecisionItem("file", "b.cs", "verify", 0.7, 1, null),
        };

        var (valid, errors) = DecisionValidator.Validate(items, Vocab, 5);

        valid.Should().HaveCount(2);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_RejectsDuplicatesUnknownActionsAndBadProbs()
    {
        var items = new[]
        {
            new DecisionItem("file", "a.cs", "act", 0.9, 0, null),
            new DecisionItem("file", "a.cs", "act", 0.8, 0, null),
            new DecisionItem("file", "b.cs", "explode", 0.8, 0, null),
            new DecisionItem("file", "c.cs", "act", double.NaN, 0, null),
            new DecisionItem("file", "d.cs", "act", 1.5, 0, null),
            new DecisionItem("file", "e.cs", "act", 0.8, 99, null),
            new DecisionItem("", "", "act", 0.8, 0, null),
        };

        var (valid, errors) = DecisionValidator.Validate(items, Vocab, 5);

        valid.Should().HaveCount(1);
        errors.Should().HaveCount(6);
    }
}
