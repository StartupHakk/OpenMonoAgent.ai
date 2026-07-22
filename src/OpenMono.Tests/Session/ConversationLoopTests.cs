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

        await loop.RunTurnAsync("Hi there", null, CancellationToken.None);

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

        await loop.RunTurnAsync("run test tool", null, CancellationToken.None);

        session.Messages.Count.Should().BeGreaterThanOrEqualTo(5);
        session.Messages.Any(m => m.Role == MessageRole.Tool).Should().BeTrue();
    }

    [Fact]
    public async Task RunTurn_InjectsQueuedFollowUpMidTurn_WithoutWaitingForTurnEnd()
    {
        var toolCallChunks = new List<StreamChunk>
        {
            new() { ToolCallDelta = new ToolCall { Id = "t1", Name = "TestTool", Arguments = "{}" }, IsComplete = false },
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

        var dequeueCalls = 0;
        var injected = new List<string>();
        var loop = new ConversationLoop(llm, tools, permissions, renderer, renderer, renderer, config, session,
            dequeuePendingUserInput: () =>
            {
                dequeueCalls++;
                return dequeueCalls == 1 ? "follow-up while working" : null;
            },
            onPendingUserInputInjected: text => injected.Add(text));

        await loop.RunTurnAsync("run test tool", null, CancellationToken.None);

        injected.Should().ContainSingle().Which.Should().Be("follow-up while working");
        session.Messages.Should().Contain(m => m.Role == MessageRole.User && m.Content == "follow-up while working");
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

        await loop.RunTurnAsync("Hello", null, CancellationToken.None);

        tracker.TotalPromptTokens.Should().Be(50);
        tracker.TotalCompletionTokens.Should().Be(20);
    }

    [Fact]
    public async Task RunManualCompactionAsync_ResetsStaleCheckpointState()
    {
        var llm = new FakeLlmClient([
            new StreamChunk { TextDelta = "summary of old turns", IsComplete = true },
        ]);

        var session = new SessionState();
        session.AddMessage(new Message { Role = MessageRole.System, Content = "System" });
        for (var i = 0; i < 6; i++)
        {
            session.AddMessage(new Message { Role = MessageRole.User, Content = $"user {i}" });
            session.AddMessage(new Message { Role = MessageRole.Assistant, Content = $"assistant {i}" });
        }

        // A checkpoint left over from earlier in the session, pointing into the message
        // list as it exists *before* this compaction rewrites it.
        session.Checkpoints.Add(new CheckpointEntry
        {
            Id = "stale", CreatedAt = DateTime.UtcNow, TurnIndex = 1, CutoffMessageIndex = 5, Summary = "stale summary",
        });
        session.CheckpointCutoffIndex = 5;

        var renderer = new TerminalRenderer();
        var config = new AppConfig();
        var loop = new ConversationLoop(llm, new ToolRegistry(), new PermissionEngine(config, renderer, renderer),
            renderer, renderer, renderer, config, session);

        await loop.RunManualCompactionAsync(null, CancellationToken.None);

        session.Checkpoints.Should().BeEmpty(
            "a full compaction re-summarises everything from raw history, so a checkpoint " +
            "pointing into the pre-compaction message list is stale and must be dropped");
        session.CheckpointCutoffIndex.Should().Be(0);
    }

    [Fact]
    public async Task DoomLoop_DoesNotFireAcrossUserTurns()
    {
        static List<StreamChunk> ToolRound(string id) =>
        [
            new() { ToolCallDelta = new ToolCall { Id = id, Name = "TestTool", Arguments = "{}" }, IsComplete = false },
            new() { IsComplete = true },
        ];
        static List<StreamChunk> TextRound() =>
        [
            new() { TextDelta = "Done.", IsComplete = false },
            new() { IsComplete = true },
        ];

        var llm = new FakeLlmClient(
            ToolRound("t1"), TextRound(),
            ToolRound("t2"), TextRound(),
            ToolRound("t3"), TextRound()
        );

        var tools = new ToolRegistry();
        tools.Register(new TestTool());
        var session = new SessionState();
        session.AddMessage(new Message { Role = MessageRole.System, Content = "System" });
        var renderer = new TerminalRenderer();
        var config = new AppConfig();
        var loop = new ConversationLoop(llm, tools, new PermissionEngine(config, renderer, renderer),
            renderer, renderer, renderer, config, session);

        await loop.RunTurnAsync("Turn 1", null, CancellationToken.None);
        await loop.RunTurnAsync("Turn 2", null, CancellationToken.None);

        await loop.RunTurnAsync("Completely different prompt after checkpoint", null, CancellationToken.None);

        session.Messages
            .Where(m => m.Role == MessageRole.User)
            .Should().NotContain(m => m.Content != null && m.Content.Contains("Doom loop detected"));
    }

    [Fact]
    public async Task DoomLoop_FiresOnAlternatingPattern_PeriodTwo()
    {
        static List<StreamChunk> Round(string toolName) =>
        [
            new() { ToolCallDelta = new ToolCall { Id = toolName, Name = toolName, Arguments = "{}" }, IsComplete = false },
            new() { IsComplete = true },
        ];


        var llm = new FakeLlmClient(
            Round("ToolA"), Round("ToolB"),
            Round("ToolA"), Round("ToolB")
        );

        var tools = new ToolRegistry();
        tools.Register(new TestTool("ToolA"));
        tools.Register(new TestTool("ToolB"));
        var session = new SessionState();
        session.AddMessage(new Message { Role = MessageRole.System, Content = "System" });
        var renderer = new TerminalRenderer();
        var config = new AppConfig();
        var loop = new ConversationLoop(llm, tools, new PermissionEngine(config, renderer, renderer),
            renderer, renderer, renderer, config, session);

        await loop.RunTurnAsync("Do the thing", null, CancellationToken.None);

        session.Messages
            .Where(m => m.Role == MessageRole.User)
            .Should().Contain(m => m.Content != null && m.Content.Contains("Doom loop detected"));
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

    private sealed class TestTool(string name = "TestTool") : ITool
    {
        public string Name => name;
        public string Description => "A test tool";
        public bool IsConcurrencySafe => true;
        public bool IsReadOnly => true;

        public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();

        public PermissionLevel RequiredPermission(JsonElement input) => PermissionLevel.AutoAllow;

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct) =>
            Task.FromResult(ToolResult.Success("test result"));
    }
}
