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
        ServedModelPicker.Pick("x", []).Should().Be("x");
    }

    [Fact]
    public void FirstCatalogIdDoesNotOverwriteASetModel()
    {
        // Foundry's /v1/models lists the whole catalog, loaded or not.
        string[] catalog = ["DeepSeek-R1-Distill-Qwen-1.5B-openvino-gpu:2", "Phi-4-mini-instruct-generic-cpu:5", "qwen3.5-9b-cuda-gpu:4"];

        ServedModelPicker.Pick("qwen3.5-9b-cuda-gpu:5", catalog).Should().Be("qwen3.5-9b-cuda-gpu:5");
        ServedModelPicker.Pick("qwen3.5-9b-cuda-gpu:5", catalog, ["qwen3.5-9b-cuda-gpu:4"]).Should().Be("qwen3.5-9b-cuda-gpu:4");
        ServedModelPicker.Pick("QWEN3.5-9B-CUDA-GPU:4", catalog, ["Phi-4-mini-instruct-generic-cpu:5"]).Should().Be("qwen3.5-9b-cuda-gpu:4");
        ServedModelPicker.Pick("", catalog, ["qwen3.5-9b-cuda-gpu:4"]).Should().Be("qwen3.5-9b-cuda-gpu:4");
    }

    [Fact]
    public void FoundryBodyOmitsPenaltiesThatLlamaCppStillGets()
    {
        var options = new LlmOptions { PresencePenalty = 1.5 };

        var foundry = JsonDocument.Parse(JsonSerializer.Serialize(
            OpenAiCompatClient.BuildRequestBody(Messages, Tools, options, "m", foundry: true), JsonOptions.Default)).RootElement;
        var llama = JsonDocument.Parse(JsonSerializer.Serialize(
            OpenAiCompatClient.BuildRequestBody(Messages, Tools, options, "m"), JsonOptions.Default)).RootElement;

        Keys(foundry).Should().NotContain(["presence_penalty", "frequency_penalty"]);
        llama.GetProperty("presence_penalty").GetDouble().Should().Be(1.5);
        Keys(llama).Should().Contain(["tools", "tool_choice", "top_k", "min_p", "repetition_penalty", "stream_options"]);
    }

    private const string ToolMarkerError =
        "chat_session.cc:1198 fl::'anonymous-namespace'::BuildToolCallContextForRequest structured tool calling requires model-published opening and closing tool markers";

    private static readonly string ToolMarkerSse =
        "data: " + JsonSerializer.Serialize(new { error = new { message = ToolMarkerError } }) + "\n\n";

    private const string HelloSse = "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\ndata: [DONE]\n\n";

    private static LlmConfig FoundryConfig(bool? toolCalling = null) => new()
    {
        Endpoint = "http://host.docker.internal:59039",
        Model = "qwen3.5-2b-generic-gpu:4",
        Backend = "foundry",
        ToolCalling = toolCalling,
    };

    private static async Task<string> RunTurnAsync(OpenAiCompatClient client)
    {
        var text = new StringBuilder();
        await foreach (var chunk in client.StreamChatAsync(Messages, Tools, new LlmOptions(), CancellationToken.None))
            text.Append(chunk.TextDelta);
        return text.ToString();
    }

    private static bool HasTools(string body) => JsonDocument.Parse(body).RootElement.TryGetProperty("tools", out _);

    [Fact]
    public async Task ToolMarkerErrorInTheStreamRetriesOnceWithoutToolsAndRemembersIt()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, ToolMarkerSse, HelloSse);
        var warnings = new List<string>();
        using var client = new OpenAiCompatClient(FoundryConfig(), new HttpClient(handler)) { OnWarning = warnings.Add };

        var first = await RunTurnAsync(client);

        first.Should().Be("hello");
        handler.Bodies.Should().HaveCount(2, "the tool request is not retried as-is");
        HasTools(handler.Bodies[0]).Should().BeTrue();
        HasTools(handler.Bodies[1]).Should().BeFalse();
        Keys(JsonDocument.Parse(handler.Bodies[1]).RootElement).Should().NotContain("tool_choice");
        SystemPrompt(handler.Bodies[1]).Should().StartWith("You are OpenMono.")
            .And.Contain("<tools>").And.Contain("\"read_file\"").And.Contain("<function=example_function_name>");
        client.StructuredToolCalling.Should().BeFalse();
        warnings.Should().ContainSingle().Which.Should().Contain("describes the tools in the prompt");

        (await RunTurnAsync(client)).Should().Be("hello");
        handler.Bodies.Should().HaveCount(3);
        HasTools(handler.Bodies[2]).Should().BeFalse("later turns in the session stay without tools");
        SystemPrompt(handler.Bodies[2]).Should().Contain("<tools>", "the text tool prompt stays on");
    }

    [Fact]
    public async Task ToolMarkerErrorAsHttp500RetriesOnceWithTheTextToolPrompt()
    {
        var handler = new RecordingHandler(
            (HttpStatusCode.InternalServerError, "structured tool calling requires model-published opening and closing tool markers"),
            (HttpStatusCode.OK, HelloSse));
        using var client = new OpenAiCompatClient(FoundryConfig(), new HttpClient(handler));

        (await RunTurnAsync(client)).Should().Be("hello");

        handler.Bodies.Should().HaveCount(2, "no same-body retry");
        HasTools(handler.Bodies[0]).Should().BeTrue();
        HasTools(handler.Bodies[1]).Should().BeFalse();
        SystemPrompt(handler.Bodies[1]).Should().Contain("<tools>");
    }

    [Fact]
    public async Task ToolMarkerErrorAsAnSseErrorEventIsDetected()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK,
            "event: error\ndata: " + JsonSerializer.Serialize(new { message = ToolMarkerError }) + "\n\n",
            HelloSse);
        using var client = new OpenAiCompatClient(FoundryConfig(), new HttpClient(handler));

        (await RunTurnAsync(client)).Should().Be("hello");

        handler.Bodies.Should().HaveCount(2);
        HasTools(handler.Bodies[1]).Should().BeFalse();
    }

    [Fact]
    public async Task FoundryProviderErrorsAreNotRetriedWithTheSameBody()
    {
        const string penalty = "Unsupported parameter: nonzero frequency_penalty and presence_penalty are not supported";
        var sse = new RecordingHandler(HttpStatusCode.OK, "data: " + JsonSerializer.Serialize(new { error = new { message = penalty } }) + "\n\n");
        using (var client = new OpenAiCompatClient(FoundryConfig(), new HttpClient(sse)))
        {
            var act = () => RunTurnAsync(client);
            (await act.Should().ThrowAsync<LlmProviderErrorException>()).Which.Message.Should().Contain("nonzero frequency_penalty");
        }

        var http500 = new RecordingHandler(HttpStatusCode.InternalServerError, "{\"error\":\"model crashed\"}");
        using (var client = new OpenAiCompatClient(FoundryConfig(), new HttpClient(http500)))
        {
            var act = () => RunTurnAsync(client);
            var thrown = (await act.Should().ThrowAsync<LlmRequestRejectedException>()).Which;
            thrown.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            thrown.Message.Should().Contain("500 (Internal Server Error)").And.Contain("model crashed");
        }

        sse.Bodies.Should().ContainSingle();
        http500.Bodies.Should().ContainSingle();
    }

    [Fact]
    public void TextToolModeReplaysHistoryWithoutOpenAiToolFields()
    {
        List<Message> history =
        [
            new() { Role = MessageRole.System, Content = "You are OpenMono." },
            new() { Role = MessageRole.User, Content = "Read the readme." },
            new()
            {
                Role = MessageRole.Assistant, Content = "Reading it.",
                ToolCalls = [new ToolCall { Id = "call_xml_0", Name = "read_file", Arguments = "{\"path\":\"README.md\"}" }],
            },
            new() { Role = MessageRole.Tool, ToolCallId = "call_xml_0", ToolName = "read_file", Content = "# OpenMono" },
        ];

        var json = JsonSerializer.Serialize(
            OpenAiCompatClient.BuildRequestBody(history, Tools, new LlmOptions(), "m", foundry: true, toolCalling: false),
            JsonOptions.Default);
        var messages = JsonDocument.Parse(json).RootElement.GetProperty("messages").EnumerateArray().ToList();

        json.Should().NotContain("tool_calls").And.NotContain("tool_call_id").And.NotContain("\"role\":\"tool\"");
        messages.Select(m => m.GetProperty("role").GetString()).Should().Equal("system", "user", "assistant", "user");
        messages[2].GetProperty("content").GetString().Should()
            .Be("Reading it.\n\n<tool_call>\n<function=read_file>\n<parameter=path>\nREADME.md\n</parameter>\n</function>\n</tool_call>");
        messages[3].GetProperty("content").GetString().Should().Be("<tool_response>\n# OpenMono\n</tool_response>");
    }

    public static TheoryData<string> TextToolCalls => new()
    {
        "<tool_call>\n<function=read_file>\n<parameter=path>\nsrc/a.cs\n</parameter>\n</function>\n</tool_call>",
        "<function=read_file><parameter=path>src/a.cs</parameter></function>",
        "<tool_call>\n{\"name\": \"read_file\", \"arguments\": {\"path\": \"src/a.cs\"}}\n</tool_call>",
        "<tools>{\"name\":\"read_file\",\"arguments\":{\"path\":\"src/a.cs\"}}</tools>",
    };

    [Theory]
    [MemberData(nameof(TextToolCalls))]
    public async Task FoundryTextToolCallsBecomeToolCallDeltas(string reply)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, ContentSse("Let me look. " + reply));
        using var client = new OpenAiCompatClient(FoundryConfig(toolCalling: false), new HttpClient(handler));

        var (text, calls) = await CollectAsync(client);

        text.Should().Be("Let me look. ", "markup is not shown as text, not even a partial \"<tool\"");
        calls.Should().ContainSingle();
        calls[0].Name.Should().Be("read_file");
        JsonDocument.Parse(calls[0].Arguments).RootElement.GetProperty("path").GetString().Should().Be("src/a.cs");
    }

    [Fact]
    public async Task TextToolParametersFollowTheSchemaType()
    {
        var tools = JsonDocument.Parse("""
            [{"type":"function","function":{"name":"read_file","parameters":{"type":"object",
              "properties":{"path":{"type":"string"},"offset":{"type":"integer"}}}}}]
            """).RootElement;
        var handler = new RecordingHandler(HttpStatusCode.OK,
            ContentSse("<function=read_file><parameter=path>42</parameter><parameter=offset>10</parameter></function>"));
        using var client = new OpenAiCompatClient(FoundryConfig(toolCalling: false), new HttpClient(handler));

        var (_, calls) = await CollectAsync(client, tools);

        var args = JsonDocument.Parse(calls.Single().Arguments).RootElement;
        args.GetProperty("path").ValueKind.Should().Be(JsonValueKind.String);
        args.GetProperty("offset").GetInt32().Should().Be(10);
    }

    [Fact]
    public async Task LlamaCppKeepsItsToolPathAndShowsHermesTextAsText()
    {
        var reply = "<tool_call>{\"name\":\"read_file\",\"arguments\":{}}</tool_call>";
        var handler = new RecordingHandler(HttpStatusCode.OK, ContentSse(reply));
        using var client = new OpenAiCompatClient(new LlmConfig { Endpoint = "http://localhost:7474", Model = "Qwen3.6-27B" }, new HttpClient(handler));

        var (text, calls) = await CollectAsync(client);

        text.Should().Be(reply);
        calls.Should().BeEmpty();
        HasTools(handler.Bodies.Single()).Should().BeTrue();
        SystemPrompt(handler.Bodies.Single()).Should().Be("You are OpenMono.");
    }

    [Fact]
    public async Task StructuredToolCallsFromFoundryAreUnchanged()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"a\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n\n";
        var handler = new RecordingHandler(HttpStatusCode.OK, sse);
        using var client = new OpenAiCompatClient(FoundryConfig(), new HttpClient(handler));

        var (_, calls) = await CollectAsync(client);

        calls.Should().ContainSingle().Which.Id.Should().Be("call_1");
        HasTools(handler.Bodies.Single()).Should().BeTrue();
        SystemPrompt(handler.Bodies.Single()).Should().Be("You are OpenMono.");
    }

    [Theory]
    [InlineData("{\"error\":{\"message\":\"boom\"}}", false, "boom")]
    [InlineData("{\"error\":\"boom\"}", false, "boom")]
    [InlineData("{\"message\":\"boom\"}", true, "boom")]
    [InlineData("boom", true, "boom")]
    [InlineData("{\"choices\":[]}", false, null)]
    public void StreamErrorsAreRecognised(string data, bool errorEvent, string? expected)
    {
        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(data); } catch (JsonException) { }

        OpenAiCompatClient.StreamErrorMessage(doc?.RootElement, data, errorEvent).Should().Be(expected);
        doc?.Dispose();
    }

    private static string ContentSse(string content)
    {
        // Split mid-marker so a partial "<tool_" arrives on its own.
        var sb = new StringBuilder();
        for (var i = 0; i < content.Length; i += 5)
        {
            var piece = content.Substring(i, Math.Min(5, content.Length - i));
            sb.Append("data: ").Append(JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = piece } } } })).Append("\n\n");
        }

        return sb.Append("data: [DONE]\n\n").ToString();
    }

    private static async Task<(string Text, List<ToolCall> Calls)> CollectAsync(OpenAiCompatClient client, JsonElement? tools = null)
    {
        var text = new StringBuilder();
        var calls = new List<ToolCall>();
        await foreach (var chunk in client.StreamChatAsync(Messages, tools ?? Tools, new LlmOptions(), CancellationToken.None))
        {
            text.Append(chunk.TextDelta);
            if (chunk.ToolCallDelta is { } call) calls.Add(call);
        }

        return (text.ToString(), calls);
    }

    private static string? SystemPrompt(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("messages").EnumerateArray()
            .Where(m => m.GetProperty("role").GetString() == "system")
            .Select(m => m.GetProperty("content").GetString()).FirstOrDefault();

    [Fact]
    public async Task ToolMarkerErrorAsHttp400AlsoRetriesWithoutTools()
    {
        var body400 = JsonSerializer.Serialize(new { error = new { message = ToolMarkerError } });
        var handler = new RecordingHandler(
            (HttpStatusCode.BadRequest, body400),
            (HttpStatusCode.OK, HelloSse));
        using var client = new OpenAiCompatClient(FoundryConfig(), new HttpClient(handler));

        (await RunTurnAsync(client)).Should().Be("hello");

        handler.Bodies.Should().HaveCount(2);
        HasTools(handler.Bodies[0]).Should().BeTrue();
        HasTools(handler.Bodies[1]).Should().BeFalse();
    }

    [Fact]
    public async Task ToolMarkerErrorWithoutToolsIsSurfacedUnretried()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, ToolMarkerSse);
        using var client = new OpenAiCompatClient(FoundryConfig(), new HttpClient(handler));

        var act = () => RunTurnAsync(client);

        var thrown = (await act.Should().ThrowAsync<LlmProviderErrorException>()).Which;
        thrown.Message.Should().StartWith("LLM API error: ").And.Contain("BuildToolCallContextForRequest");
        handler.Bodies.Should().HaveCount(2, "one request with tools, one without, then no blind retries");
    }

    [Fact]
    public async Task LlamaCppKeepsToolsButDoesNotRetryAToolMarkerError()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, ToolMarkerSse);
        using var client = new OpenAiCompatClient(new LlmConfig { Endpoint = "http://localhost:7474", Model = "Qwen3.6-27B" }, new HttpClient(handler));

        var act = () => RunTurnAsync(client);

        await act.Should().ThrowAsync<LlmProviderErrorException>();
        handler.Bodies.Should().ContainSingle();
        HasTools(handler.Bodies[0]).Should().BeTrue();
        client.StructuredToolCalling.Should().BeTrue();
    }

    [Fact]
    public void ProviderErrorsAreNotReportedAsUnreachable()
    {
        var foundry = FoundryConfig();
        var providerError = new LlmProviderErrorException(ToolMarkerError);

        LlmErrorHints.IsTransportFailure(providerError).Should().BeFalse();
        LlmErrorHints.IsTransportFailure(new HttpRequestException("LLM API error: model is still loading")).Should().BeFalse();
        var hint = LlmErrorHints.ForProviderError(providerError.Message, foundry);
        hint.Should().Contain("Foundry Local is reachable").And.Contain("structured tool calling")
            .And.Contain("foundry server logs").And.NotContain("not reachable");
        LlmErrorHints.ForProviderError("LLM API error: something else", foundry)
            .Should().Contain("it is reachable").And.NotContain("not reachable");
        LlmErrorHints.For(HttpStatusCode.BadRequest, foundry, new LlmRequestRejectedException(ToolMarkerError).Message)
            .Should().Contain("structured tool calling");
    }

    [Fact]
    public void ConnectionFailuresAreStillUnreachable()
    {
        var refused = new HttpRequestException(
            "Connection refused (host.docker.internal:59039)",
            new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused));

        LlmErrorHints.IsTransportFailure(refused).Should().BeTrue();
        LlmErrorHints.IsTransportFailure(new HttpRequestException("Failed to connect after retries")).Should().BeTrue();
        LlmErrorHints.IsTransportFailure(new HttpRequestException("HTTP 503", null, HttpStatusCode.ServiceUnavailable)).Should().BeFalse();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly (HttpStatusCode Status, string Body)[] _responses;

        public RecordingHandler(HttpStatusCode status, params string[] bodies) =>
            _responses = bodies.Select(b => (status, b)).ToArray();

        public RecordingHandler(params (HttpStatusCode Status, string Body)[] responses) => _responses = responses;

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var (status, body) = _responses[Math.Min(Bodies.Count, _responses.Length) - 1];
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, status == HttpStatusCode.OK ? "text/event-stream" : "application/json"),
            };
        }
    }
}
