using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using TgAttribute = Terminal.Gui.Drawing.Attribute;

namespace OpenMono.Tui.Rendering;

public enum BlockKind
{
    Text,
    Heading,
    CodeBlock,
    InlineCode,
    ListItem,
    BlockQuote,
    HorizontalRule
}

public readonly record struct StyledSpan(string Text, TgAttribute Attribute);

public sealed class RenderedBlock
{
    public BlockKind Kind { get; init; }
    public List<StyledSpan> Spans { get; init; } = [];
    public int IndentLevel { get; init; }

    public string? Language { get; init; }
    public string? RawCode { get; init; }
    public List<ColoredSpan>? HighlightedSpans { get; init; }
}

public static class MarkdownRenderer
{

    private static Theme T => ThemeManager.Current;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    public static List<RenderedBlock> Render(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        var blocks = new List<RenderedBlock>();

        foreach (var block in document)
        {
            RenderBlock(block, blocks, indent: 0);
        }

        return blocks;
    }

    public static bool HasIncompleteCodeFence(string markdown)
    {
        var fenceCount = 0;
        var i = 0;
        while (i < markdown.Length)
        {
            if (i + 2 < markdown.Length && markdown[i] == '`' && markdown[i + 1] == '`' && markdown[i + 2] == '`')
            {
                fenceCount++;
                i += 3;

                while (i < markdown.Length && markdown[i] != '\n') i++;
            }
            else
            {
                i++;
            }
        }
        return fenceCount % 2 != 0;
    }

    private static void RenderBlock(Block block, List<RenderedBlock> result, int indent)
    {
        switch (block)
        {
            case HeadingBlock heading:
                RenderHeading(heading, result);
                break;

            case ParagraphBlock paragraph:
                RenderParagraph(paragraph, result, indent);
                break;

            case FencedCodeBlock fenced:
                RenderFencedCode(fenced, result, indent);
                break;

            case CodeBlock code:
                RenderPlainCode(code, result, indent);
                break;

            case ListBlock list:
                RenderList(list, result, indent);
                break;

            case QuoteBlock quote:
                RenderQuote(quote, result, indent);
                break;

            case ThematicBreakBlock:
                result.Add(new RenderedBlock
                {
                    Kind = BlockKind.HorizontalRule,
                    Spans = [new StyledSpan(new string('\u2500', 60), T.Dim)],
                    IndentLevel = indent
                });
                break;

            case ContainerBlock container:
                foreach (var child in container)
                    RenderBlock(child, result, indent);
                break;
        }
    }

    private static void RenderHeading(HeadingBlock heading, List<RenderedBlock> result)
    {
        var spans = new List<StyledSpan>();
        var prefix = heading.Level switch
        {
            1 => "# ",
            2 => "## ",
            3 => "### ",
            _ => new string('#', heading.Level) + " "
        };

        spans.Add(new StyledSpan(prefix, T.Heading));

        if (heading.Inline is not null)
            CollectInlineSpans(heading.Inline, spans, T.Heading);

        result.Add(new RenderedBlock
        {
            Kind = BlockKind.Heading,
            Spans = spans
        });
    }

    private static void RenderParagraph(ParagraphBlock paragraph, List<RenderedBlock> result, int indent)
    {
        var spans = new List<StyledSpan>();

        if (paragraph.Inline is not null)
            CollectInlineSpans(paragraph.Inline, spans, T.Normal);

        result.Add(new RenderedBlock
        {
            Kind = BlockKind.Text,
            Spans = spans,
            IndentLevel = indent
        });
    }

    private static void RenderFencedCode(FencedCodeBlock fenced, List<RenderedBlock> result, int indent)
    {
        var language = fenced.Info?.Trim();
        var code = ExtractCodeBlockText(fenced);

        List<ColoredSpan>? highlighted = null;
        if (!string.IsNullOrEmpty(language))
        {
            var spans = SyntaxHighlighter.Highlight(code, language);
            if (spans.Count > 0 && spans.Any(s => s.Token != TokenType.Plain))
                highlighted = spans;
        }

        result.Add(new RenderedBlock
        {
            Kind = BlockKind.CodeBlock,
            Language = language,
            RawCode = code,
            HighlightedSpans = highlighted,
            Spans = [new StyledSpan(code, T.Normal)],
            IndentLevel = indent
        });
    }

    private static void RenderPlainCode(CodeBlock code, List<RenderedBlock> result, int indent)
    {
        var text = ExtractCodeBlockText(code);
        result.Add(new RenderedBlock
        {
            Kind = BlockKind.CodeBlock,
            RawCode = text,
            Spans = [new StyledSpan(text, T.Normal)],
            IndentLevel = indent
        });
    }

    private static void RenderList(ListBlock list, List<RenderedBlock> result, int indent)
    {
        var index = 1;
        foreach (var item in list)
        {
            if (item is not ListItemBlock listItem) continue;

            var bullet = list.IsOrdered ? $"{index}. " : "\u2022 ";
            var first = true;

            foreach (var child in listItem)
            {
                if (child is ParagraphBlock para)
                {
                    var spans = new List<StyledSpan>();
                    if (first)
                    {
                        spans.Add(new StyledSpan(bullet, T.Bullet));
                        first = false;
                    }

                    if (para.Inline is not null)
                        CollectInlineSpans(para.Inline, spans, T.Normal);

                    result.Add(new RenderedBlock
                    {
                        Kind = BlockKind.ListItem,
                        Spans = spans,
                        IndentLevel = indent + 1
                    });
                }
                else
                {
                    RenderBlock(child, result, indent + 1);
                }
            }

            index++;
        }
    }

    private static void RenderQuote(QuoteBlock quote, List<RenderedBlock> result, int indent)
    {
        foreach (var child in quote)
        {
            if (child is ParagraphBlock para)
            {
                var spans = new List<StyledSpan> { new("\u2502 ", T.Dim) };

                if (para.Inline is not null)
                    CollectInlineSpans(para.Inline, spans, T.Quote);

                result.Add(new RenderedBlock
                {
                    Kind = BlockKind.BlockQuote,
                    Spans = spans,
                    IndentLevel = indent + 1
                });
            }
            else
            {
                RenderBlock(child, result, indent + 1);
            }
        }
    }

    private static void CollectInlineSpans(ContainerInline container, List<StyledSpan> spans, TgAttribute defaultAttr)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    spans.Add(new StyledSpan(literal.Content.ToString(), defaultAttr));
                    break;

                case EmphasisInline emphasis:
                    var emphAttr = emphasis.DelimiterCount >= 2 ? T.Bold : T.Dim;
                    CollectInlineSpans(emphasis, spans, emphAttr);
                    break;

                case CodeInline code:
                    spans.Add(new StyledSpan(code.Content, T.InlineCode));
                    break;

                case LinkInline link:
                    CollectInlineSpans(link, spans, T.Link);
                    if (link.Url is not null)
                        spans.Add(new StyledSpan($" ({link.Url})", T.Dim));
                    break;

                case LineBreakInline:
                    spans.Add(new StyledSpan("\n", defaultAttr));
                    break;

                case ContainerInline nested:
                    CollectInlineSpans(nested, spans, defaultAttr);
                    break;

                default:

                    var text = inline.ToString();
                    if (!string.IsNullOrEmpty(text))
                        spans.Add(new StyledSpan(text, defaultAttr));
                    break;
            }
        }
    }

    private static string ExtractCodeBlockText(LeafBlock block)
    {
        if (block.Lines.Count == 0)
            return "";

        var lines = new List<string>();
        for (var i = 0; i < block.Lines.Count; i++)
        {
            var line = block.Lines.Lines[i];
            lines.Add(line.Slice.ToString());
        }

        return string.Join('\n', lines).TrimEnd();
    }
}
