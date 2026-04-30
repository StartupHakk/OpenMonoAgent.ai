using FluentAssertions;
using OpenMono.Tui.Rendering;

namespace OpenMono.Tests.Tui;

public class SyntaxHighlighterTests
{
    [Fact]
    public void UnknownLanguage_ReturnsPlain()
    {
        var spans = SyntaxHighlighter.Highlight("hello world", "unknown_lang");
        spans.Should().HaveCount(1);
        spans[0].Token.Should().Be(TokenType.Plain);
        spans[0].Start.Should().Be(0);
        spans[0].Length.Should().Be(11);
    }

    [Theory]
    [InlineData("csharp")]
    [InlineData("cs")]
    [InlineData("c#")]
    public void CSharp_KeywordsHighlighted(string lang)
    {
        var spans = SyntaxHighlighter.Highlight("public class Foo { }", lang);
        var keywords = spans.Where(s => s.Token == TokenType.Keyword).ToList();
        keywords.Should().Contain(s => GetText("public class Foo { }", s) == "public");
        keywords.Should().Contain(s => GetText("public class Foo { }", s) == "class");
    }

    [Fact]
    public void CSharp_StringsHighlighted()
    {
        var code = "var x = \"hello\";";
        var spans = SyntaxHighlighter.Highlight(code, "csharp");
        var strings = spans.Where(s => s.Token == TokenType.String).ToList();
        strings.Should().NotBeEmpty();
        strings.Should().Contain(s => GetText(code, s).Contains("hello"));
    }

    [Fact]
    public void CSharp_CommentsHighlighted()
    {
        var code = "// this is a comment\nvar x = 1;";
        var spans = SyntaxHighlighter.Highlight(code, "csharp");
        var comments = spans.Where(s => s.Token == TokenType.Comment).ToList();
        comments.Should().NotBeEmpty();
        comments.Should().Contain(s => GetText(code, s).Contains("this is a comment"));
    }

    [Fact]
    public void CSharp_NumbersHighlighted()
    {
        var code = "int x = 42;";
        var spans = SyntaxHighlighter.Highlight(code, "csharp");
        spans.Should().Contain(s => s.Token == TokenType.Number && GetText(code, s) == "42");
    }

    [Fact]
    public void CSharp_FunctionsHighlighted()
    {
        var code = "Console.WriteLine(\"hi\");";
        var spans = SyntaxHighlighter.Highlight(code, "csharp");
        spans.Should().Contain(s => s.Token == TokenType.Function && GetText(code, s) == "WriteLine");
    }

    [Fact]
    public void Python_KeywordsHighlighted()
    {
        var code = "def hello():\n    return True";
        var spans = SyntaxHighlighter.Highlight(code, "python");
        var keywords = spans.Where(s => s.Token == TokenType.Keyword).ToList();
        keywords.Should().Contain(s => GetText(code, s) == "def");
        keywords.Should().Contain(s => GetText(code, s) == "return");
        keywords.Should().Contain(s => GetText(code, s) == "True");
    }

    [Fact]
    public void Python_CommentsHighlighted()
    {
        var code = "x = 1 # this is a comment";
        var spans = SyntaxHighlighter.Highlight(code, "python");
        spans.Should().Contain(s => s.Token == TokenType.Comment && GetText(code, s).Contains("this is a comment"));
    }

    [Fact]
    public void Json_KeysAreKeywords()
    {
        var code = "{\"name\": \"Alice\", \"age\": 30}";
        var spans = SyntaxHighlighter.Highlight(code, "json");
        var keys = spans.Where(s => s.Token == TokenType.Keyword).ToList();
        keys.Should().Contain(s => GetText(code, s).Contains("name"));
        keys.Should().Contain(s => GetText(code, s).Contains("age"));
    }

    [Fact]
    public void Json_NumbersHighlighted()
    {
        var code = "{\"count\": 42}";
        var spans = SyntaxHighlighter.Highlight(code, "json");
        spans.Should().Contain(s => s.Token == TokenType.Number && GetText(code, s) == "42");
    }

    [Fact]
    public void Bash_CommentsHighlighted()
    {
        var code = "#!/bin/bash\n# comment\necho hello";
        var spans = SyntaxHighlighter.Highlight(code, "bash");
        spans.Where(s => s.Token == TokenType.Comment).Should().NotBeEmpty();
    }

    [Fact]
    public void DetectLanguage_FromFenceLine()
    {
        SyntaxHighlighter.DetectLanguage("```csharp").Should().Be("csharp");
        SyntaxHighlighter.DetectLanguage("```python").Should().Be("python");
        SyntaxHighlighter.DetectLanguage("```js").Should().Be("javascript");
        SyntaxHighlighter.DetectLanguage("```ts").Should().Be("typescript");
        SyntaxHighlighter.DetectLanguage("```").Should().BeNull();
    }

    [Fact]
    public void CommentsOverrideKeywords()
    {

        var code = "// public class Foo";
        var spans = SyntaxHighlighter.Highlight(code, "csharp");
        spans.Should().Contain(s => s.Token == TokenType.Comment);
        spans.Should().NotContain(s => s.Token == TokenType.Keyword,
            "keywords inside comments should not be highlighted");
    }

    [Fact]
    public void SpansCoverEntireInput()
    {
        var code = "public void Method() { return; }";
        var spans = SyntaxHighlighter.Highlight(code, "csharp");
        var totalCovered = spans.Sum(s => s.Length);
        totalCovered.Should().Be(code.Length, "spans should cover every character");
    }

    [Fact]
    public void SpansDoNotOverlap()
    {
        var code = "var x = \"hello\" + 42; // comment";
        var spans = SyntaxHighlighter.Highlight(code, "csharp");
        var sorted = spans.OrderBy(s => s.Start).ToList();

        for (var i = 1; i < sorted.Count; i++)
        {
            var prev = sorted[i - 1];
            (prev.Start + prev.Length).Should().BeLessThanOrEqualTo(sorted[i].Start,
                $"span {i-1} ({prev.Start}+{prev.Length}) should not overlap span {i} ({sorted[i].Start})");
        }
    }

    [Theory]
    [InlineData("go")]
    [InlineData("rust")]
    [InlineData("sql")]
    [InlineData("yaml")]
    [InlineData("typescript")]
    public void AllLanguages_ProduceNonEmptySpans(string lang)
    {
        var code = lang switch
        {
            "go" => "func main() { fmt.Println(\"hello\") }",
            "rust" => "fn main() { println!(\"hello\"); }",
            "sql" => "SELECT * FROM users WHERE id = 1;",
            "yaml" => "name: test\ncount: 42",
            "typescript" => "const x: string = \"hello\";",
            _ => "x = 1"
        };

        var spans = SyntaxHighlighter.Highlight(code, lang);
        spans.Should().NotBeEmpty();
        spans.Sum(s => s.Length).Should().Be(code.Length);
    }

    private static string GetText(string source, ColoredSpan span) =>
        source.Substring(span.Start, span.Length);
}
