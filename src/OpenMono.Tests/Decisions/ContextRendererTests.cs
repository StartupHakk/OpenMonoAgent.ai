using FluentAssertions;
using OpenMono.Decisions;

namespace OpenMono.Tests.Decisions;

public class ContextRendererTests
{
    [Fact]
    public void RenderTask_EmitsTaskAndContextLines()
    {
        var rendered = ContextRenderer.RenderTask(
            "Compare tools",
            new Dictionary<string, string?> { ["goal"] = "briefing", ["notes"] = "none yet" });

        rendered.Should().StartWith("TASK Compare tools");
        rendered.Should().Contain("CONTEXT goal=briefing");
        rendered.Should().Contain("CONTEXT notes=none yet");
    }

    [Fact]
    public void RenderTask_TruncatesLongValues()
    {
        var rendered = ContextRenderer.RenderTask(
            "t",
            new Dictionary<string, string?> { ["evidence"] = new string('x', 5000) });

        rendered.Length.Should().BeLessThan(3000);
    }

    [Fact]
    public void RenderElement_EmitsKindLabelAndValue()
    {
        var rendered = ContextRenderer.RenderElement("Edit", "Phone number", "(503) 555-0142");

        rendered.Should().Be("ELEMENT Edit \"Phone number\" value=\"(503) 555-0142\"");
    }

    [Fact]
    public void TruncateUtf8Safe_NeverSplitsEmoji()
    {
        var rendered = ContextRenderer.TruncateUtf8Safe("ab\uD83D\uDE00cd", 3);

        rendered.Should().Be("ab");
    }

    [Fact]
    public void TruncateUtf8Safe_KeepsCompleteCjk()
    {
        var rendered = ContextRenderer.TruncateUtf8Safe("日本語テスト", 4);

        rendered.Should().Be("日本語テ");
    }

    [Fact]
    public void TruncateUtf8Safe_LeavesShortTextUntouched()
    {
        ContextRenderer.TruncateUtf8Safe("hello", 512).Should().Be("hello");
    }
}
