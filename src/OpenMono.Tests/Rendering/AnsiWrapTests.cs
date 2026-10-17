using FluentAssertions;
using OpenMono.Rendering;

namespace OpenMono.Tests.Rendering;

public class AnsiWrapTests
{
    [Fact]
    public void Wrap_ShortText_Unchanged()
    {
        AnsiPainter.Wrap("hello", 20).Should().Equal(["hello"]);
    }

    [Fact]
    public void Wrap_LongText_BreaksAtWordBoundary_NoCharsLost()
    {
        const string text = "Allow dangerous Bash? rm -rf /tmp/cache --verbose --force";
        var lines = AnsiPainter.Wrap(text, 20);

        lines.Should().HaveCountGreaterThan(1);
        // Breaks happen at spaces, so rejoining restores the original.
        string.Join(" ", lines).Should().Be(text);
        foreach (var line in lines)
            line.Length.Should().BeLessThanOrEqualTo(20);
    }

    [Fact]
    public void Wrap_LongWordWithoutSpaces_HardBreaks()
    {
        var lines = AnsiPainter.Wrap(new string('x', 50), 20);

        lines.Should().HaveCount(3);
        lines.Should().OnlyContain(l => l.Length <= 20);
    }

    [Fact]
    public void Wrap_PreservesExplicitNewlines()
    {
        var lines = AnsiPainter.Wrap("Steps: a, b\nTools: c", 80);

        lines.Should().Equal(["Steps: a, b", "Tools: c"]);
    }

    [Fact]
    public void Wrap_MultilineWraps_EveryLine()
    {
        var lines = AnsiPainter.Wrap($"Steps: {new string('s', 100)}\nTools: tiny", 30);

        lines.Should().HaveCountGreaterThan(2);
        lines.Should().OnlyContain(l => l.Length <= 30);
        lines.Last().Should().Be("Tools: tiny");
    }
}
