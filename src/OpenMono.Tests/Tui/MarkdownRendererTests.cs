using FluentAssertions;
using OpenMono.Tui.Rendering;

namespace OpenMono.Tests.Tui;

public class MarkdownRendererTests
{
    private static void SkipIfNoTerminalGui()
    {
        try { _ = OpenMono.Tui.Rendering.ThemeManager.Current; }
        catch { Skip.If(true, "Terminal.Gui module init failed in test runner"); }
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Render_PlainText_ProducesTextBlock()
    {
        SkipIfNoTerminalGui();
        var blocks = MarkdownRenderer.Render("Hello world");
        blocks.Should().HaveCount(1);
        blocks[0].Kind.Should().Be(BlockKind.Text);
        blocks[0].Spans.Should().Contain(s => s.Text.Contains("Hello world"));
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Render_Heading_ProducesHeadingBlock()
    {
        SkipIfNoTerminalGui();
        var blocks = MarkdownRenderer.Render("# Title");
        blocks.Should().ContainSingle(b => b.Kind == BlockKind.Heading);
        blocks[0].Spans.Should().Contain(s => s.Text.Contains("Title"));
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Render_FencedCodeBlock_DetectsLanguage()
    {
        var md = "```csharp\npublic class Foo { }\n```";
        SkipIfNoTerminalGui();
        var blocks = MarkdownRenderer.Render(md);
        var codeBlock = blocks.FirstOrDefault(b => b.Kind == BlockKind.CodeBlock);
        codeBlock.Should().NotBeNull();
        codeBlock!.Language.Should().Be("csharp");
        codeBlock.RawCode.Should().Contain("public class Foo");
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Render_FencedCodeBlock_WithHighlighting()
    {
        var md = "```csharp\npublic void Main() { }\n```";
        SkipIfNoTerminalGui();
        var blocks = MarkdownRenderer.Render(md);
        var codeBlock = blocks.First(b => b.Kind == BlockKind.CodeBlock);
        codeBlock.HighlightedSpans.Should().NotBeNull("known language should produce highlighted spans");
        codeBlock.HighlightedSpans!.Should().Contain(s => s.Token == TokenType.Keyword);
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Render_BoldText_ProducesBoldSpan()
    {
        SkipIfNoTerminalGui();
        var blocks = MarkdownRenderer.Render("This is **bold** text");
        blocks.Should().HaveCount(1);
        var spans = blocks[0].Spans;
        spans.Should().Contain(s => s.Text == "bold" && s.Attribute.Style == Terminal.Gui.Drawing.TextStyle.Bold);
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Render_InlineCode_ProducesInlineCodeSpan()
    {
        SkipIfNoTerminalGui();
        var blocks = MarkdownRenderer.Render("Use `Console.WriteLine` here");
        var spans = blocks.SelectMany(b => b.Spans).ToList();
        spans.Should().Contain(s => s.Text == "Console.WriteLine");
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Render_UnorderedList_ProducesListItems()
    {
        var md = "- First\n- Second\n- Third";
        SkipIfNoTerminalGui();
        var blocks = MarkdownRenderer.Render(md);
        var items = blocks.Where(b => b.Kind == BlockKind.ListItem).ToList();
        items.Should().HaveCount(3);
        items[0].Spans.Should().Contain(s => s.Text.Contains("First"));
        items[0].Spans.Should().Contain(s => s.Text.Contains("\u2022"));
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Render_OrderedList_ProducesNumberedItems()
    {
        var md = "1. Alpha\n2. Beta";
        SkipIfNoTerminalGui();
        var blocks = MarkdownRenderer.Render(md);
        var items = blocks.Where(b => b.Kind == BlockKind.ListItem).ToList();
        items.Should().HaveCount(2);
        items[0].Spans.Should().Contain(s => s.Text.Contains("1."));
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Render_BlockQuote_ProducesQuoteBlock()
    {
        SkipIfNoTerminalGui();
        var blocks = MarkdownRenderer.Render("> This is quoted text");
        blocks.Should().Contain(b => b.Kind == BlockKind.BlockQuote);
        var quote = blocks.First(b => b.Kind == BlockKind.BlockQuote);
        quote.Spans.Should().Contain(s => s.Text.Contains("This is quoted text"));
        quote.Spans.Should().Contain(s => s.Text.Contains("\u2502"));
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Render_HorizontalRule_ProducesHrBlock()
    {
        SkipIfNoTerminalGui();
        var blocks = MarkdownRenderer.Render("---");
        blocks.Should().Contain(b => b.Kind == BlockKind.HorizontalRule);
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Render_Link_ProducesLinkSpan()
    {
        SkipIfNoTerminalGui();
        var blocks = MarkdownRenderer.Render("[OpenMono](https://example.com)");
        var spans = blocks.SelectMany(b => b.Spans).ToList();
        spans.Should().Contain(s => s.Text.Contains("OpenMono"));
        spans.Should().Contain(s => s.Text.Contains("https://example.com"));
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Render_Mixed_ProducesMultipleBlocks()
    {
        var md = """
            # Heading

            Some text with **bold**.

            ```python
            print("hello")
            ```

            - Item 1
            - Item 2
            """;

        SkipIfNoTerminalGui();
        var blocks = MarkdownRenderer.Render(md);
        blocks.Should().Contain(b => b.Kind == BlockKind.Heading);
        blocks.Should().Contain(b => b.Kind == BlockKind.Text);
        blocks.Should().Contain(b => b.Kind == BlockKind.CodeBlock);
        blocks.Should().Contain(b => b.Kind == BlockKind.ListItem);
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void Render_EmptyString_ProducesNoBlocks()
    {
        SkipIfNoTerminalGui();
        var blocks = MarkdownRenderer.Render("");
        blocks.Should().BeEmpty();
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void HasIncompleteCodeFence_DetectsOpen()
    {
        MarkdownRenderer.HasIncompleteCodeFence("```python\nprint('hi')").Should().BeTrue();
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void HasIncompleteCodeFence_CompleteFence()
    {
        MarkdownRenderer.HasIncompleteCodeFence("```python\nprint('hi')\n```").Should().BeFalse();
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void HasIncompleteCodeFence_NoFence()
    {
        MarkdownRenderer.HasIncompleteCodeFence("just plain text").Should().BeFalse();
    }
}
