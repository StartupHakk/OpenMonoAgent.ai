using System.Net;
using System.Text;
using FluentAssertions;
using OpenMono.Llm;
using OpenMono.Session;

namespace OpenMono.Tests.Llm;

public class AnthropicClientTests
{
    [Fact]
    public async Task StreamChatAsync_ReportsPromptTokensFromMessageStart()
    {
        // Anthropic puts input_tokens on the message_start event, and final
        // output_tokens + stop_reason on message_delta.
        var sse =
            "data: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":1234,\"output_tokens\":1}}}\n\n" +
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\n" +
            "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":5}}\n\n" +
            "data: {\"type\":\"message_stop\"}\n\n";

        using var http = new HttpClient(new StubHandler(sse));
        using var client = new AnthropicClient(new ProviderConfig { Name = "anthropic", ApiKey = "test" }, http);

        var messages = new List<Message> { new() { Role = MessageRole.User, Content = "hello" } };

        var chunks = new List<StreamChunk>();
        await foreach (var c in client.StreamChatAsync(messages, null, new LlmOptions { Model = "claude" }, CancellationToken.None))
            chunks.Add(c);

        chunks.Should().Contain(c => c.Usage != null && c.Usage.PromptTokens == 1234,
            "input_tokens from message_start must be surfaced so compaction does not fall back to a crude estimate");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        public StubHandler(string body) => _body = body;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "text/event-stream"),
            });
    }
}
