using FluentAssertions;
using OpenMono.Session;

namespace OpenMono.Tests.Session;

/// <summary>
/// Guards the llama.cpp 400 "Assistant message must contain either 'content'
/// or 'tool_calls'!": empty assistant messages must never reach the wire.
/// </summary>
public class MessageSanitizerTests
{
    private static Message Assistant(string? content, List<ToolCall>? calls = null) =>
        new() { Role = MessageRole.Assistant, Content = content, ToolCalls = calls };

    [Fact]
    public void EmptyAssistantMessage_IsDropped()
    {
        var messages = new List<Message>
        {
            new() { Role = MessageRole.User, Content = "do the thing" },
            Assistant(null),
        };

        var sanitized = MessageSanitizer.SanitizeForRequest(messages);

        sanitized.Should().HaveCount(1);
        sanitized[0].Role.Should().Be(MessageRole.User);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ContentlessAssistantMessage_WithoutCalls_NeverSurvives(string? content)
    {
        var sanitized = MessageSanitizer.SanitizeForRequest([Assistant(content)]);

        sanitized.Should().BeEmpty();
    }

    [Fact]
    public void AssistantMessage_WithCallsAndNoContent_Survives()
    {
        // Tool-call-only assistant messages are valid and must be kept.
        var messages = new List<Message>
        {
            Assistant(null, [new ToolCall { Id = "c1", Name = "Bash", Arguments = "{}" }]),
        };

        var sanitized = MessageSanitizer.SanitizeForRequest(messages);

        sanitized.Should().HaveCount(1);
    }

    [Fact]
    public void TextAssistantMessage_SurvivesUntouched()
    {
        var msg = Assistant("here is the plan");
        var messages = new List<Message> { msg };

        var sanitized = MessageSanitizer.SanitizeForRequest(messages);

        sanitized.Should().ContainSingle().Which.Should().BeSameAs(msg);
    }

    [Fact]
    public void InputList_IsNotMutated()
    {
        var messages = new List<Message>
        {
            new() { Role = MessageRole.User, Content = "hi" },
            Assistant(null),
        };

        MessageSanitizer.SanitizeForRequest(messages);

        messages.Should().HaveCount(2);
    }

    [Fact]
    public void DropPreserves_OrderOfSurvivors()
    {
        var messages = new List<Message>
        {
            new() { Role = MessageRole.User, Content = "one" },
            Assistant(null),
            Assistant("two"),
            Assistant("   "),
            Assistant("three"),
        };

        var sanitized = MessageSanitizer.SanitizeForRequest(messages);

        sanitized.Select(m => m.Content).Should().Equal("one", "two", "three");
    }
}
