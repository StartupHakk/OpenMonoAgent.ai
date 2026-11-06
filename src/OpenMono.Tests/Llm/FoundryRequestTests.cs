using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Llm;
using OpenMono.Session;
using OpenMono.Utils;
using Xunit;

namespace OpenMono.Tests.Llm;

public sealed class FoundryRequestTests
{
    private static readonly string[] LlamaCppExtensions =
        ["top_k", "min_p", "repetition_penalty", "presence_penalty", "chat_template_kwargs", "reasoning_effort", "stream_options"];

    private static readonly JsonElement Tools = JsonDocument.Parse("""
        [{"type":"function","function":{"name":"read_file","description":"Read a file",
          "parameters":{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}}}]
        """).RootElement;

    private static readonly List<Message> Messages =
    [
        new() { Role = MessageRole.System, Content = "You are OpenMono." },
        new() { Role = MessageRole.User, Content = "Can you tell me about this project?" },
    ];

    private static readonly LlmOptions ThinkingOptions = new()
    {
        Model = "qwen3.5-2b-generic-cpu:3",
        EnableThinking = true,
        ReasoningEffort = "high",
        PreserveThinking = true,
        MinP = 0.05,
    };

    private static JsonElement Body(bool foundry, bool toolCalling = true) =>
        JsonDocument.Parse(JsonSerializer.Serialize(
            OpenAiCompatClient.BuildRequestBody(Messages, Tools, ThinkingOptions, "unused", foundry, toolCalling),
            JsonOptions.Default)).RootElement;

    private static IEnumerable<string> Keys(JsonElement body) => body.EnumerateObject().Select(p => p.Name);

    [Fact]
    public void FoundryBodyHasStandardOpenAiFieldsOnly()
    {
        var body = Body(foundry: true);

        Keys(body).Should().BeEquivalentTo("model", "messages", "temperature", "top_p", "max_tokens", "stream", "tools", "tool_choice");
        Keys(body).Should().NotContain(LlamaCppExtensions);
        body.GetProperty("model").GetString().Should().Be("qwen3.5-2b-generic-cpu:3");
        body.GetProperty("stream").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void FoundryBodyLeavesToolsOutWhenTheModelCannotCallThem()
    {
        var body = Body(foundry: true, toolCalling: false);

        Keys(body).Should().NotContain(["tools", "tool_choice"]);
    }

    [Fact]
    public void LlamaCppBodyKeepsItsExtensions()
    {
        var body = Body(foundry: false);

        Keys(body).Should().Contain(LlamaCppExtensions).And.Contain(["tools", "tool_choice"]);
        body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean().Should().BeTrue();
        body.GetProperty("reasoning_effort").GetString().Should().Be("high");
    }

    [Fact]
    public void LlamaCppBodyKeepsToolsEvenWhenToolCallingIsOff()
    {
        Keys(Body(foundry: false, toolCalling: false)).Should().Contain("tools");
    }

    [Fact]
    public async Task FoundryClientPostsNoLlamaCppFields()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "data: [DONE]\n\n");
        var config = new LlmConfig { Endpoint = "http://host.docker.internal:7474", Model = "qwen3.5-2b-generic-cpu:3", Backend = "foundry" };
        using var client = new OpenAiCompatClient(config, new HttpClient(handler));

        await foreach (var _ in client.StreamChatAsync(Messages, Tools, ThinkingOptions, CancellationToken.None))
        {
        }

        var sent = JsonDocument.Parse(handler.Bodies.Single()).RootElement;
        Keys(sent).Should().NotContain(LlamaCppExtensions);
        Keys(sent).Should().Contain("tools");
    }

    [Fact]
    public async Task BadRequestSurfacesTheServerReplyWithoutRetrying()
    {
        var reply = """{"error":{"message":"Unsupported parameter: 'min_p' is not supported with this model.","type":"invalid_request_error"}}""";
        var handler = new RecordingHandler(HttpStatusCode.BadRequest, reply);
        var config = new LlmConfig { Endpoint = "http://host.docker.internal:7474", Model = "deepseek-r1", Backend = "foundry" };
        using var client = new OpenAiCompatClient(config, new HttpClient(handler));

        var act = async () =>
        {
            await foreach (var _ in client.StreamChatAsync(Messages, Tools, new LlmOptions(), CancellationToken.None))
            {
            }
        };

        var thrown = (await act.Should().ThrowAsync<LlmRequestRejectedException>()).Which;
        thrown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        thrown.Message.Should().Contain("400 (Bad Request)").And.Contain("Unsupported parameter: 'min_p'");
        thrown.ProviderBody.Should().Be(reply);
        handler.Bodies.Should().HaveCount(1, "a 400 is deterministic, so it is not retried");
    }

    [Fact]
    public void LongServerRepliesAreTruncated()
    {
        var ex = new LlmRequestRejectedException(new string('x', 5000));

        ex.Message.Length.Should().BeLessThan(LlmRequestRejectedException.MaxBodyChars + 120);
        ex.Message.Should().EndWith("…");
    }

    [Fact]
    public void FoundryHintsPointAtTheWindowsHost()
    {
        var hint = LlmErrorHints.For(HttpStatusCode.BadRequest, new LlmConfig { Backend = "foundry" });

        hint.Should().Contain("Foundry Local rejected the request (400)")
            .And.Contain("served model")
            .And.Contain("request field")
            .And.Contain(@".\win\openmono.ps1 start")
            .And.Contain("foundry server logs")
            .And.NotContain("docker logs llama-server");
        LlmErrorHints.For(HttpStatusCode.InternalServerError, new LlmConfig { Backend = "foundry" })
            .Should().Contain("foundry server logs");
    }

    [Fact]
    public void LlamaServerHintsStayOnLlamaServer()
    {
        var hint = LlmErrorHints.For(HttpStatusCode.BadRequest, new LlmConfig());

        hint.Should().Contain("llama-server rejected the request (400)")
            .And.Contain("served model")
            .And.Contain("docker logs llama-server")
            .And.Contain("/new");
    }

    [Fact]
    public void ConfiguredModelIsKeptWhenTheServerListsSeveral()
    {
        string[] served = ["DeepSeek-R1-Distill-Qwen-1.5B-openvino-gpu:2", "qwen3.5-2b-generic-cpu:3"];

        ServedModelPicker.Pick("qwen3.5-2b-generic-cpu:3", served).Should().Be("qwen3.5-2b-generic-cpu:3");
        ServedModelPicker.Pick("", served).Should().Be("DeepSeek-R1-Distill-Qwen-1.5B-openvino-gpu:2");
        ServedModelPicker.Pick("not-served", ["Qwen3.6-27B"]).Should().Be("Qwen3.6-27B");
        ServedModelPicker.Pick("x", []).Should().BeNull();
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, status == HttpStatusCode.OK ? "text/event-stream" : "application/json"),
            };
        }
    }
}
