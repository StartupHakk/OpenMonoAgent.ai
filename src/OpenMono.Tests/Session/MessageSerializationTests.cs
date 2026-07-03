using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Session;

namespace OpenMono.Tests.Session;

public class MessageSerializationTests
{
    [Fact]
    public void Message_WithContentParts_RoundTripsThroughJson()
    {
        var message = new Message
        {
            Role = MessageRole.User,
            ContentParts = new List<ContentPart>
            {
                new TextPart("describe this image"),
                new ImagePart("data:image/png;base64,AAAA"),
            },
        };

        var json = JsonSerializer.Serialize(message, JsonOptions.Default);
        var restored = JsonSerializer.Deserialize<Message>(json, JsonOptions.Default);

        restored.Should().NotBeNull();
        restored!.ContentParts.Should().HaveCount(2);
        restored.ContentParts![0].Should().BeOfType<TextPart>()
            .Which.Text.Should().Be("describe this image");
        restored.ContentParts![1].Should().BeOfType<ImagePart>()
            .Which.Url.Should().Be("data:image/png;base64,AAAA");
    }
}
