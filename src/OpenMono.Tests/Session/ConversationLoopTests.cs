using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using OpenMono.Config;
using OpenMono.Llm;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Session;

public class ConversationLoopTests
{
    [Fact]
    public async Task RunTurn_TextOnly_AddsMessages()
    {
        var llm = new FakeLlmClient([
            new StreamChunk { TextDelta = "Hello!", IsComplete = false },
            new StreamChunk { IsComplete = true, Usage = new UsageInfo { PromptTokens = 10, CompletionTokens = 5 } },
        ]);

        var tools = new ToolRegistry();
        var session = new SessionState();
        session.AddMessage(new Message { Role = MessageRole.System, Content = "System prompt" });

        var renderer = new TerminalRenderer();
        var config = new AppConfig();
        var permissions = new PermissionEngine(config, renderer, renderer);
        var loop = new ConversationLoop(llm, tools, permissions, renderer, renderer, renderer, config, session);

        await loop.RunTurnAsync("Hi there", CancellationToken.None);

        session.Messages.Should().HaveCount(3);
        session.Messages[0].Role.Should().Be(MessageRole.System);
        session.Messages[1].Role.Should().Be(MessageRole.User);
        session.Messages[1].Content.Should().Be("Hi there");
        session.Messages[2].Role.Should().Be(MessageRole.Assistant);
        session.Messages[2].Content.Should().Be("Hello!");
    }

    [Fact]
    public async Task RunTurn_WithToolCall_ExecutesTool()
    {
        var toolCallChunks = new List<StreamChunk>
        {
            new()
            {
                ToolCallDelta = new ToolCall { Id = "t1", Name = "TestTool", Arguments = "{}" },
                IsComplete = false
            },
            new() { IsComplete = true },
        };
        var textChunks = new List<StreamChunk>
        {
            new() { TextDelta = "Done!", IsComplete = false },
            new() { IsComplete = true },
        };

        var llm = new FakeLlmClient(toolCallChunks, textChunks);
        var tools = new ToolRegistry();
        tools.Register(new TestTool());

        var session = new SessionState();
        session.AddMessage(new Message { Role = MessageRole.System, Content = "System" });

        var renderer = new TerminalRenderer();
        var config = new AppConfig();
        var permissions = new PermissionEngine(config, renderer, renderer);
        var loop = new ConversationLoop(llm, tools, permissions, renderer, renderer, renderer, config, session);

        await loop.RunTurnAsync("run test tool", CancellationToken.None);

        session.Messages.Count.Should().BeGreaterThanOrEqualTo(5);
        session.Messages.Any(m => m.Role == MessageRole.Tool).Should().BeTrue();
    }

    [Fact]
    public async Task RunTurn_IncrementsTokens()
    {
        var llm = new FakeLlmClient([
            new StreamChunk { TextDelta = "Hi", IsComplete = false },
            new StreamChunk { IsComplete = true, Usage = new UsageInfo { PromptTokens = 50, CompletionTokens = 20 } },
        ]);

        var session = new SessionState();
        session.AddMessage(new Message { Role = MessageRole.System, Content = "System" });

        var tracker = new TokenTracker();
        session.Meta.TokenTracker = tracker;

        var renderer = new TerminalRenderer();
        var config = new AppConfig();
        var loop = new ConversationLoop(llm, new ToolRegistry(), new PermissionEngine(config, renderer, renderer), renderer, renderer, renderer, config, session);

        await loop.RunTurnAsync("Hello", CancellationToken.None);

        tracker.TotalPromptTokens.Should().Be(50);
        tracker.TotalCompletionTokens.Should().Be(20);
    }

    private sealed class FakeLlmClient : ILlmClient
    {
        private readonly List<List<StreamChunk>> _rounds;
        private int _roundIndex;

        public FakeLlmClient(params List<StreamChunk>[] rounds)
        {
            _rounds = [.. rounds];
        }

        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages,
            JsonElement? tools,
            LlmOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            var chunks = _roundIndex < _rounds.Count ? _rounds[_roundIndex] : [new StreamChunk { TextDelta = "", IsComplete = true }];
            _roundIndex++;

            foreach (var chunk in chunks)
            {
                yield return chunk;
                await Task.Yield();
            }
        }

        public void Dispose() { }
    }

    private sealed class TestTool : ITool
    {
        public string Name => "TestTool";
        public string Description => "A test tool";
        public bool IsConcurrencySafe => true;
        public bool IsReadOnly => true;

        public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

        public PermissionLevel RequiredPermission(JsonElement input) => PermissionLevel.AutoAllow;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct) =>
            Task.FromResult(ToolResult.Success("test result"));
    }
}
