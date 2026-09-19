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

    [Fact]
    public void Validate_RejectsEmptyMenu()
    {
        var (valid, errors) = DecisionValidator.Validate([], Vocab, 5);

        valid.Should().BeEmpty();
        errors.Should().Equal("menu-empty");
    }

    [Fact]
    public void Validate_RejectsOverflowMenu()
    {
        var items = Enumerable.Range(0, 256).Select(i => new DecisionItem("file", $"f{i}.cs", "act", 0.9, i, null)).ToList();

        var (valid, errors) = DecisionValidator.Validate(items, Vocab, 300);

        valid.Should().HaveCount(256);
        errors.Should().Equal("menu-overflow:256");
    }

    [Fact]
    public void Validate_RejectsEmptyDetail()
    {
        var items = new[] { new DecisionItem("file", "a.cs", "act", 0.9, 0, "") };

        var (valid, errors) = DecisionValidator.Validate(items, Vocab, 5);

        valid.Should().BeEmpty();
        errors.Should().Equal("empty-value:a.cs");
    }

    [Fact]
    public void ValidateChoice_RejectsUnnormalizedProbs()
    {
        var probs = new Dictionary<string, double>(StringComparer.Ordinal) { ["a"] = 0.3, ["b"] = 0.2 };
        var options = new[] { ("a", (string?)"First"), ("b", (string?)"Second") };

        var errors = DecisionValidator.ValidateChoice("Pick one", options, "a", probs);

        errors.Should().ContainSingle().Which.Should().StartWith("probs-not-normalized:");
    }

    [Fact]
    public void ValidateChoice_RejectsNonArgmax()
    {
        var probs = new Dictionary<string, double>(StringComparer.Ordinal) { ["a"] = 0.2, ["b"] = 0.8 };
        var options = new[] { ("a", (string?)"First"), ("b", (string?)"Second") };

        var errors = DecisionValidator.ValidateChoice("Pick one", options, "a", probs);

        errors.Should().Equal("choice-not-argmax:a");
    }

    [Fact]
    public void ValidateChoice_AcceptsValidMenu()
    {
        var probs = new Dictionary<string, double>(StringComparer.Ordinal) { ["a"] = 0.2, ["b"] = 0.8 };
        var options = new[] { ("a", (string?)"First"), ("b", (string?)"Second") };

        DecisionValidator.ValidateChoice("Pick one", options, "b", probs).Should().BeEmpty();
    }

    [Fact]
    public void ValidateLevels_RejectsBadCountsAndBlanks()
    {
        DecisionValidator.ValidateLevels(["only"]).Should().Equal("levels-out-of-range:1");
        DecisionValidator.ValidateLevels(["a", ""]).Should().Equal("empty-value");
    }
}
