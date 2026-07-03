using FluentAssertions;
using OpenMono.Rendering;
using Spectre.Console;

namespace OpenMono.Tests.Rendering;

public class TerminalRendererTests
{
    private static (TerminalRenderer Renderer, StringWriter Sink) MakeRenderer()
    {
        var sink = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(sink),
        });
        return (new TerminalRenderer(console), sink);
    }

    [Fact]
    public void WriteToolDiff_RendersAddedAndRemovedLines()
    {
        var (renderer, sink) = MakeRenderer();
        var diff = "--- a/f.txt\n+++ b/f.txt\n@@ -1,1 +1,1 @@\n-oldcontent\n+newcontent";

        renderer.WriteToolDiff(diff);

        var output = sink.ToString();
        output.Should().Contain("oldcontent");
        output.Should().Contain("newcontent");
    }

    [Fact]
    public void WriteToolContent_RendersFilePathAndContent()
    {
        var (renderer, sink) = MakeRenderer();

        renderer.WriteToolContent("FileRead", "src/app.cs", "public class App { }");

        var output = sink.ToString();
        output.Should().Contain("app.cs");
        output.Should().Contain("public class App");
    }
}
