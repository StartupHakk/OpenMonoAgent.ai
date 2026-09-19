using FluentAssertions;
using OpenMono.Decisions;

namespace OpenMono.Tests.Decisions;

public class ChoiceMenuTests
{
    private static IReadOnlyList<(string Key, string? Description)> Items(params (string, string?)[] items) => items;

    [Fact]
    public void Build_AppendsOtherOnce()
    {
        var menu = ChoiceMenu.Build("next_worker", Items(("research", "Collect evidence"), ("write", "Draft text")));

        menu.Should().ContainKey(ChoiceMenu.OtherKey);
        menu.Keys.Should().HaveCount(3);
        menu.Keys.Should().ContainInOrder("research", "write", ChoiceMenu.OtherKey);
    }

    [Fact]
    public void Build_KeepsExistingOtherWithoutDuplicating()
    {
        var menu = ChoiceMenu.Build("route", Items(("a", "First"), (ChoiceMenu.OtherKey, "Custom escape")));

        menu.Keys.Count(k => k == ChoiceMenu.OtherKey).Should().Be(1);
        menu[ChoiceMenu.OtherKey].Should().Be("Custom escape");
    }

    [Fact]
    public void Build_OmitsOtherWhenExcluded()
    {
        var menu = ChoiceMenu.Build("strict", Items(("a", "First")), includeOther: false);

        menu.Should().NotContainKey(ChoiceMenu.OtherKey);
    }

    [Fact]
    public void Build_RejectsEmptyAndOverflow()
    {
        Action empty = () => ChoiceMenu.Build("empty", Items());
        empty.Should().Throw<InvalidOperationException>().WithMessage("menu-empty");

        var many = Enumerable.Range(0, 256).Select(i => ($"k{i}", (string?)$"d{i}")).ToList();
        Action overflow = () => ChoiceMenu.Build("big", many);
        overflow.Should().Throw<InvalidOperationException>().WithMessage("menu-overflow:256");
    }

    [Fact]
    public void Build_Accepts255Options()
    {
        var many = Enumerable.Range(0, 255).Select(i => ($"k{i}", (string?)$"d{i}")).ToList();

        var menu = ChoiceMenu.Build("max", many);

        menu.Should().ContainKey(ChoiceMenu.OtherKey);
    }

    [Fact]
    public void Filter_IncludesThenExcludes()
    {
        var items = Items(("decide_rank", "Rank"), ("decide_verify", "Verify"), ("Bash", "Shell"));

        var kept = ChoiceMenu.Filter(items, "decide_*", "decide_verify");

        kept.Select(i => i.Key).Should().Equal("decide_rank");
    }

    [Fact]
    public void Filter_EmptyPatternsKeepAll()
    {
        var items = Items(("a", "First"), ("b", "Second"));

        ChoiceMenu.Filter(items, null, "").Select(i => i.Key).Should().Equal("a", "b");
    }

    [Fact]
    public void Filter_RejectsOverflowAfterFilter()
    {
        var many = Enumerable.Range(0, 300).Select(i => ($"k{i}", (string?)$"d{i}")).ToList();

        Action overflow = () => ChoiceMenu.Filter(many, null, null);
        overflow.Should().Throw<InvalidOperationException>().WithMessage("menu-overflow:300");
    }

    [Fact]
    public void Filter_MatchesCaseInsensitively()
    {
        var items = Items(("Bash", "Shell"));

        ChoiceMenu.Filter(items, "bash*", null).Select(i => i.Key).Should().Equal("Bash");
    }
}
