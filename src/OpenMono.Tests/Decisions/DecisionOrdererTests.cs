using FluentAssertions;
using OpenMono.Decisions;

namespace OpenMono.Tests.Decisions;

public class DecisionOrdererTests
{
    [Fact]
    public void Order_DropsSkipAndLowConfidence()
    {
        var items = new[]
        {
            new DecisionItem("t", "a", "act", 0.9, 0, null),
            new DecisionItem("t", "b", "skip", 0.99, 1, null),
            new DecisionItem("t", "c", "act", 0.1, 2, null),
        };

        var (ordered, skipped) = DecisionOrderer.Order(items, 0.5, true);

        ordered.Select(i => i.Id).Should().Equal("a");
        skipped.Select(i => i.Id).Should().Equal("b", "c");
    }

    [Fact]
    public void Order_KeepsAtMostOneTerminalAction()
    {
        var items = new[]
        {
            new DecisionItem("t", "a", "done", 0.9, 0, null),
            new DecisionItem("t", "b", "done", 0.95, 1, null),
        };

        var (ordered, skipped) = DecisionOrderer.Order(items, 0.5, true);

        ordered.Should().HaveCount(1);
        skipped.Should().HaveCount(1);
    }

    [Fact]
    public void Order_DropsTerminalWhenNotAllowed()
    {
        var items = new[] { new DecisionItem("t", "a", "done", 0.9, 0, null) };

        var (ordered, skipped) = DecisionOrderer.Order(items, 0.5, false);

        ordered.Should().BeEmpty();
        skipped.Select(i => i.Id).Should().Equal("a");
    }

    [Fact]
    public void Order_SortsActBeforeVerifyBeforeDoneAndKeepsInputOrderWithinTier()
    {
        var items = new[]
        {
            new DecisionItem("t", "done1", "done", 0.9, 0, null),
            new DecisionItem("t", "v1", "verify", 0.9, 1, null),
            new DecisionItem("t", "a2", "act", 0.9, 2, null),
            new DecisionItem("t", "a1", "act", 0.9, 3, null),
        };

        var (ordered, _) = DecisionOrderer.Order(items, 0.5, true);

        ordered.Select(i => i.Id).Should().Equal("a2", "a1", "v1", "done1");
    }
}
