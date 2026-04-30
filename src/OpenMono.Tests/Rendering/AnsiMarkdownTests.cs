using FluentAssertions;
using OpenMono.Rendering;

namespace OpenMono.Tests.Rendering;

public class AnsiMarkdownTests
{
    [Fact]
    public void Render_WrapsLongLines()
    {
        var text = "This is a very long line that should be wrapped into multiple lines based on the provided width.";
        var width = 20;
        var lines = AnsiMarkdown.Render(text, width);

        lines.Should().HaveCountGreaterThan(1);
        foreach (var line in lines)
        {
            AnsiMarkdown.VisLen(line).Should().BeLessThanOrEqualTo(width);
        }
    }

    [Fact]
    public void Render_PreservesAnsiWhileWrapping()
    {
        var text = "This is **bold** and `code` in a long line that wraps.";
        var width = 20;
        var lines = AnsiMarkdown.Render(text, width);

        var combined = string.Join("\n", lines);
        combined.Should().Contain("\x1b[1m");
        combined.Should().Contain("\x1b[40m\x1b[36m");
    }

    [Fact]
    public void Render_HandlesUnorderedListsWithWrapping()
    {
        var text = "- This is a long list item that should be wrapped correctly with indentation.";
        var width = 30;
        var lines = AnsiMarkdown.Render(text, width);

        lines.Should().HaveCountGreaterThan(1);
        AnsiMarkdown.VisLen(lines[0]).Should().BeLessThanOrEqualTo(width);
        AnsiMarkdown.StripAnsi(lines[0]).Should().StartWith("• ");
        AnsiMarkdown.StripAnsi(lines[1]).Should().StartWith("  ");
    }

    [Fact]
    public void Render_HandlesCodeBlocksWithWrapping()
    {
        var md = "```\nThis is a long line inside a code block that should also be wrapped if it is too long for the width.\n```";
        var width = 40;
        var lines = AnsiMarkdown.Render(md, width);

        lines.Should().Contain(l => l.Contains("╭───"));
        lines.Should().Contain(l => l.Contains("╰───"));
        var contentLines = lines.Where(l => !l.Contains("╭───") && !l.Contains("╰───")).ToList();
        contentLines.Should().HaveCountGreaterThan(1);
    }
}
