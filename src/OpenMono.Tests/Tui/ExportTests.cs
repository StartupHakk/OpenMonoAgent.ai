using System.Text.Json;
using FluentAssertions;
using OpenMono.Session;
using OpenMono.Tui.Export;

namespace OpenMono.Tests.Tui;

public class ExportTests
{
    private static SessionState MakeSession()
    {
        var session = new SessionState();
        session.AddMessage(new Message
        {
            Role = MessageRole.System,
            Content = "You are an assistant."
        });
        session.AddMessage(new Message
        {
            Role = MessageRole.User,
            Content = "Fix the bug in TokenTracker.cs"
        });
        session.AddMessage(new Message
        {
            Role = MessageRole.Assistant,
            Content = "I'll look at the file.\n\n```csharp\npublic int Total => Prompt + Completion;\n```",
            ToolCalls =
            [
                new ToolCall { Id = "t1", Name = "FileRead", Arguments = "{\"path\":\"src/TokenTracker.cs\"}" }
            ]
        });
        session.AddMessage(new Message
        {
            Role = MessageRole.Tool,
            ToolCallId = "t1",
            ToolName = "FileRead",
            Content = "public class TokenTracker { ... }"
        });
        session.TurnCount = 1;
        return session;
    }

    [Fact]
    public void MarkdownExport_ContainsSessionHeader()
    {
        var session = MakeSession();
        var md = MarkdownExporter.Export(session);

        md.Should().Contain("# OpenMono Session");
        md.Should().Contain(session.Id);
        md.Should().Contain("Turns:");
    }

    [Fact]
    public void MarkdownExport_ContainsUserAndAssistantMessages()
    {
        var md = MarkdownExporter.Export(MakeSession());
        md.Should().Contain("## User");
        md.Should().Contain("Fix the bug in TokenTracker.cs");
        md.Should().Contain("## Assistant");
        md.Should().Contain("I'll look at the file");
    }

    [Fact]
    public void MarkdownExport_ContainsToolCalls()
    {
        var md = MarkdownExporter.Export(MakeSession());
        md.Should().Contain("**Tool call:** `FileRead`");
        md.Should().Contain("\"path\"");
    }

    [Fact]
    public void MarkdownExport_ContainsToolResults()
    {
        var md = MarkdownExporter.Export(MakeSession());
        md.Should().Contain("### Tool: FileRead");
        md.Should().Contain("public class TokenTracker");
    }

    [Fact]
    public void MarkdownExport_SkipsSystemMessages()
    {
        var md = MarkdownExporter.Export(MakeSession());
        md.Should().NotContain("You are an assistant");
    }

    [Fact]
    public void MarkdownExport_PreservesCodeBlocks()
    {
        var md = MarkdownExporter.Export(MakeSession());
        md.Should().Contain("```csharp");
        md.Should().Contain("public int Total");
    }

    [Fact]
    public void JsonExport_IsValidJson()
    {
        var json = JsonExporter.Export(MakeSession());
        var act = () => JsonDocument.Parse(json);
        act.Should().NotThrow();
    }

    [Fact]
    public void JsonExport_ContainsAllMessages()
    {
        var json = JsonExporter.Export(MakeSession());
        using var doc = JsonDocument.Parse(json);

        var messages = doc.RootElement.GetProperty("messages");
        messages.GetArrayLength().Should().Be(4);
    }

    [Fact]
    public void JsonExport_ContainsSessionMetadata()
    {
        var session = MakeSession();
        var json = JsonExporter.Export(session);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("id").GetString().Should().Be(session.Id);
        doc.RootElement.GetProperty("turn_count").GetInt32().Should().Be(1);
    }

    [Fact]
    public void JsonExport_ContainsToolCallData()
    {
        var json = JsonExporter.Export(MakeSession());
        json.Should().Contain("FileRead");
        json.Should().Contain("TokenTracker.cs");
    }

    [Fact]
    public void HtmlExport_IsValidHtml()
    {
        var html = HtmlExporter.Export(MakeSession());
        html.Should().StartWith("<!DOCTYPE html>");
        html.Should().Contain("<html");
        html.Should().Contain("</html>");
    }

    [Fact]
    public void HtmlExport_ContainsStyledMessages()
    {
        var html = HtmlExporter.Export(MakeSession());
        html.Should().Contain("class=\"message user\"");
        html.Should().Contain("class=\"message assistant\"");
        html.Should().Contain("class=\"message tool\"");
    }

    [Fact]
    public void HtmlExport_ContainsCodeBlocks()
    {
        var html = HtmlExporter.Export(MakeSession());
        html.Should().Contain("<pre><code");
        html.Should().Contain("public int Total");
    }

    [Fact]
    public void HtmlExport_EscapesHtml()
    {
        var session = new SessionState();
        session.AddMessage(new Message
        {
            Role = MessageRole.User,
            Content = "<script>alert('xss')</script>"
        });

        var html = HtmlExporter.Export(session);
        html.Should().NotContain("<script>");
        html.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void HtmlExport_SkipsSystemMessages()
    {
        var html = HtmlExporter.Export(MakeSession());
        html.Should().NotContain("You are an assistant");
    }

    [Fact]
    public void HtmlExport_ContainsCss()
    {
        var html = HtmlExporter.Export(MakeSession());
        html.Should().Contain("<style>");
        html.Should().Contain("font-family");
    }
}
