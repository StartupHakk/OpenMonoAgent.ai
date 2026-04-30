using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Llm;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Integration;

public class SmokeTests
{
    private static readonly string? TestEndpoint =
        Environment.GetEnvironmentVariable("OPENMONO_TEST_ENDPOINT");

    private static readonly string TestModel =
        Environment.GetEnvironmentVariable("OPENMONO_TEST_MODEL") ?? "";

    [SkippableFact]
    public async Task LlmClient_StreamsTextResponse()
    {
        var endpoint = await GetReachableEndpoint();
        Skip.If(endpoint is null, "No LLM endpoint reachable");

        var config = new LlmConfig { Endpoint = endpoint, Model = TestModel };
        using var client = new OpenAiCompatClient(config);

        var messages = new List<Message>
        {
            new() { Role = MessageRole.System, Content = "You are a helpful assistant. Reply in one short sentence." },
            new() { Role = MessageRole.User, Content = "What is 2+2?" },
        };

        var options = new LlmOptions { Model = TestModel, MaxTokens = 100, Temperature = 0.1 };
        var response = "";

        await foreach (var chunk in client.StreamChatAsync(messages, tools: null, options, CancellationToken.None))
        {
            if (chunk.TextDelta is not null)
                response += chunk.TextDelta;
        }

        response.Should().NotBeNullOrWhiteSpace("LLM should return text");
        response.Should().Contain("4", "LLM should know 2+2=4");
    }

    [SkippableFact]
    public async Task LlmClient_HandlesToolCall()
    {
        var endpoint = await GetReachableEndpoint();
        Skip.If(endpoint is null, "No LLM endpoint reachable");

        var config = new LlmConfig { Endpoint = endpoint, Model = TestModel };
        using var client = new OpenAiCompatClient(config);

        var toolDefs = JsonDocument.Parse("""
        [
            {
                "type": "function",
                "function": {
                    "name": "get_weather",
                    "description": "Get the weather for a city",
                    "parameters": {
                        "type": "object",
                        "properties": {
                            "city": { "type": "string" }
                        },
                        "required": ["city"]
                    }
                }
            }
        ]
        """).RootElement;

        var messages = new List<Message>
        {
            new() { Role = MessageRole.System, Content = "You are an assistant with access to a weather tool. Use it to answer weather questions." },
            new() { Role = MessageRole.User, Content = "What's the weather in Tokyo?" },
        };

        var options = new LlmOptions { Model = TestModel, MaxTokens = 200, Temperature = 0.1 };
        var toolCalls = new List<ToolCall>();
        var text = "";

        await foreach (var chunk in client.StreamChatAsync(messages, toolDefs, options, CancellationToken.None))
        {
            if (chunk.TextDelta is not null) text += chunk.TextDelta;
            if (chunk.ToolCallDelta is not null) toolCalls.Add(chunk.ToolCallDelta);
        }

        (toolCalls.Count > 0 || text.Length > 0).Should().BeTrue(
            "LLM should return either a tool call or text response");
    }

    [SkippableFact]
    public async Task FullConversationLoop_EndToEnd()
    {
        var endpoint = await GetReachableEndpoint();
        Skip.If(endpoint is null, "No LLM endpoint reachable");

        var config = new AppConfig();
        config.Llm.Endpoint = endpoint;
        config.Llm.Model = TestModel;
        config.Llm.MaxOutputTokens = 200;

        var llmConfig = new LlmConfig { Endpoint = endpoint, Model = TestModel, MaxOutputTokens = 200 };
        using var client = new OpenAiCompatClient(llmConfig);

        var tools = new ToolRegistry();
        tools.Register(new FileReadTool());
        tools.Register(new GlobTool());

        var session = new SessionState();
        session.AddMessage(new Message { Role = MessageRole.System, Content = "You are a coding assistant. Be very brief." });

        var renderer = new TerminalRenderer();
        var permissions = new PermissionEngine(config, renderer, renderer);
        var loop = new ConversationLoop(client, tools, permissions, renderer, renderer, renderer, config, session);

        await loop.RunTurnAsync("Say hello in one word.", CancellationToken.None);

        session.Messages.Count.Should().BeGreaterThanOrEqualTo(3);
        session.Messages.Last().Role.Should().Be(MessageRole.Assistant);
        session.Messages.Last().Content.Should().NotBeNullOrWhiteSpace();
    }

    private static async Task<string?> GetReachableEndpoint()
    {
        var candidates = new List<string>();
        if (TestEndpoint is not null)
            candidates.Add(TestEndpoint);

        candidates.AddRange([
            "http://localhost:7474",
            "http://localhost:11434",
        ]);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

        foreach (var endpoint in candidates)
        {
            try
            {

                var response = await http.GetAsync($"{endpoint}/health");
                if (response.IsSuccessStatusCode)
                    return endpoint;
            }
            catch { }

            try
            {
                var response = await http.GetAsync($"{endpoint}/api/tags");
                if (response.IsSuccessStatusCode)
                    return endpoint;
            }
            catch { }
        }

        return null;
    }
}
