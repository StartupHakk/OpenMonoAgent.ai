using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Commands;
using OpenMono.Config;
using OpenMono.Llm;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;
using OpenMono.Tui;
using OpenMono.Tui.Export;
using OpenMono.Tui.Rendering;

namespace OpenMono.Tests.Tui;

[Trait("Category", "TuiIntegration")]
public class TuiIntegrationTests
{

    [Fact]
    public async Task ConversationLoop_WithPause_PreservesAllTokens()
    {
        var chunks = Enumerable.Range(0, 20)
            .Select(i => new StreamChunk { TextDelta = $"word{i} ", IsComplete = false })
            .Append(new StreamChunk { IsComplete = true, Usage = new UsageInfo { PromptTokens = 100, CompletionTokens = 20 } })
            .ToList();

        var llm = new FakeLlmClient([chunks]);
        var session = new SessionState();
        session.AddMessage(new Message { Role = MessageRole.System, Content = "System" });

        var renderer = new TerminalRenderer();
        var config = new AppConfig();
        var pause = new PauseController();
        var loop = new ConversationLoop(
            llm, new ToolRegistry(), new PermissionEngine(config, renderer, renderer),
            renderer, renderer, renderer, config, session, pauseController: pause);

        _ = Task.Run(async () =>
        {
            await Task.Delay(20);
            pause.TogglePause();
            await Task.Delay(100);
            pause.TogglePause();
        });

        await loop.RunTurnAsync("Hello", CancellationToken.None);

        var assistant = session.Messages.Last(m => m.Role == MessageRole.Assistant);
        assistant.Content.Should().NotBeNull();

        for (var i = 0; i < 20; i++)
            assistant.Content.Should().Contain($"word{i}", $"word{i} should survive pause/resume");
    }

    [Fact]
    public async Task ConversationLoop_WithPause_CancellationDuringPause()
    {
        var chunks = Enumerable.Range(0, 100)
            .Select(i => new StreamChunk { TextDelta = $"w{i} ", IsComplete = false })
            .Append(new StreamChunk { IsComplete = true })
            .ToList();

        var llm = new FakeLlmClient([chunks]);
        var session = new SessionState();
        session.AddMessage(new Message { Role = MessageRole.System, Content = "System" });

        var renderer = new TerminalRenderer();
        var config = new AppConfig();
        var pause = new PauseController();
        using var cts = new CancellationTokenSource();

        var loop = new ConversationLoop(
            llm, new ToolRegistry(), new PermissionEngine(config, renderer, renderer),
            renderer, renderer, renderer, config, session, pauseController: pause);

        _ = Task.Run(async () =>
        {
            await Task.Delay(10);
            pause.TogglePause();
            await Task.Delay(50);
            cts.Cancel();
        });

        var act = () => loop.RunTurnAsync("Hello", cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void TokenTracker_OnUsageUpdated_Fires()
    {
        var tracker = new TokenTracker();
        var calls = new List<(int prompt, int completion)>();
        tracker.OnUsageUpdated = (p, c) => calls.Add((p, c));

        tracker.RecordUsage(100, 50);
        tracker.RecordUsage(200, 75);

        calls.Should().HaveCount(2);
        calls[0].Should().Be((100, 50));
        calls[1].Should().Be((300, 125));
    }

    [Fact]
    public async Task ApprovalController_FullWorkflow()
    {
        var ac = new ApprovalController();
        var decisions = new Queue<ApprovalDecision>();
        decisions.Enqueue(ApprovalDecision.Allow);
        decisions.Enqueue(ApprovalDecision.Deny);
        decisions.Enqueue(ApprovalDecision.AllowAll);

        ac.RequestApprovalFunc = (_, _) => Task.FromResult(decisions.Dequeue());
        ac.ToggleApprovalMode();

        var call = new ToolCall { Id = "t1", Name = "Test", Arguments = "{}" };

        var r1 = await ac.CheckApprovalAsync(call, CancellationToken.None);
        r1.Should().Be(ApprovalDecision.Allow);

        var r2 = await ac.CheckApprovalAsync(call, CancellationToken.None);
        r2.Should().Be(ApprovalDecision.Deny);

        var r3 = await ac.CheckApprovalAsync(call, CancellationToken.None);
        r3.Should().Be(ApprovalDecision.Allow);

        var r4 = await ac.CheckApprovalAsync(call, CancellationToken.None);
        r4.Should().Be(ApprovalDecision.Allow);
        decisions.Should().BeEmpty("AllowAll should have skipped remaining dialog calls");
    }

    [Fact]
    public void ExportCommand_Markdown_RoundTrip()
    {
        var session = MakeConversationSession();
        var md = MarkdownExporter.Export(session);

        md.Should().Contain("## User");
        md.Should().Contain("## Assistant");
        md.Should().Contain("Hello there");
        md.Should().Contain("I can help");
        md.Length.Should().BeGreaterThan(100);
    }

    [Fact]
    public void ExportCommand_Json_RoundTrip()
    {
        var session = MakeConversationSession();
        var json = JsonExporter.Export(session);

        using var doc = JsonDocument.Parse(json);
        var id = doc.RootElement.GetProperty("id").GetString();
        id.Should().Be(session.Id);

        var messages = doc.RootElement.GetProperty("messages");
        messages.GetArrayLength().Should().Be(session.Messages.Count);
    }

    [Fact]
    public void ExportCommand_Html_ContainsAllMessages()
    {
        var session = MakeConversationSession();
        var html = HtmlExporter.Export(session);

        html.Should().Contain("Hello there");
        html.Should().Contain("I can help");
        html.Should().Contain("class=\"message user\"");
        html.Should().Contain("class=\"message assistant\"");
    }

    [Fact]
    public void ExportCommand_WritesToDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"openmono-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        try
        {
            var session = MakeConversationSession();
            var path = Path.Combine(dir, "test.md");
            File.WriteAllText(path, MarkdownExporter.Export(session));

            File.Exists(path).Should().BeTrue();
            var content = File.ReadAllText(path);
            content.Should().Contain(session.Id);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static void SkipIfNoTerminalGui()
    {
        try { _ = OpenMono.Tui.Rendering.ThemeManager.Current; }
        catch { Skip.If(true, "Terminal.Gui module init failed in test runner"); }
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void SyntaxHighlighter_UsesThemeColors()
    {
        SkipIfNoTerminalGui();
        ThemeManager.Load(null);
        var attr = SyntaxHighlighter.GetAttribute(TokenType.Keyword);
        var expected = ThemeManager.Current.GetSyntaxAttribute(TokenType.Keyword);
        attr.Foreground.Should().Be(expected.Foreground);
    }

    [SkippableFact(typeof(TypeInitializationException))]
    public void ThemeSwitching_ChangesSyntaxColors()
    {
        SkipIfNoTerminalGui();
        var darkKeyword = ThemeManager.Dark.SyntaxKeyword;
        var lightKeyword = ThemeManager.Light.SyntaxKeyword;
        darkKeyword.Should().NotBe(lightKeyword, "dark and light themes should have different keyword colors");
    }

    [Fact]
    public void MessageDataStorage_10K_Messages_FastAdd()
    {

        var messages = new List<Message>();
        var sw = Stopwatch.StartNew();

        for (var i = 0; i < 10_000; i++)
        {
            messages.Add(new Message
            {
                Role = i % 3 == 0 ? MessageRole.User : MessageRole.Assistant,
                Content = $"Message number {i} with some content that wraps around the screen."
            });
        }

        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(500, "creating 10K Message records should be fast");
        messages.Should().HaveCount(10_000);
    }

    [Fact]
    public void HeightCalculation_10K_Messages()
    {
        var sw = Stopwatch.StartNew();
        var totalHeight = 0;

        for (var i = 0; i < 10_000; i++)
        {
            var text = $"Message {i}: {new string('x', 80)}";
            var lines = 0;
            foreach (var line in text.Split('\n'))
                lines += Math.Max(1, (int)Math.Ceiling((double)line.Length / 76));
            totalHeight += lines + 2;
        }

        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(100, "10K height calculations should complete in <100ms");
        totalHeight.Should().BeGreaterThan(0);
    }

    [Fact]
    public void BinarySearch_FindsCorrectEntry()
    {

        var cumulativeY = new int[1000];
        var heights = new int[1000];
        var y = 0;
        for (var i = 0; i < 1000; i++)
        {
            heights[i] = 3 + (i % 5);
            cumulativeY[i] = y;
            y += heights[i];
        }

        var target = 500;
        var lo = 0;
        var hi = 999;
        var result = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (cumulativeY[mid] + heights[mid] <= target)
            {
                lo = mid + 1;
                result = lo;
            }
            else
            {
                hi = mid - 1;
            }
        }

        result.Should().BeInRange(0, 999);
        cumulativeY[result].Should().BeLessThanOrEqualTo(target);
        (cumulativeY[result] + heights[result]).Should().BeGreaterThan(target);
    }

    [Fact]
    public void StreamingMetrics_IntegrationWithTokenTracker()
    {
        var metrics = new StreamingMetrics();
        var tracker = new TokenTracker();

        metrics.OnStreamStart();

        for (var i = 1; i <= 50; i++)
        {
            tracker.RecordUsage(0, 1);
            metrics.OnTokenReceived(tracker.TotalCompletionTokens);
            if (i % 10 == 0)
                Thread.Sleep(10);
        }

        metrics.OnStreamEnd();

        metrics.TotalCompletionTokens.Should().Be(50);
        metrics.IsStreaming.Should().BeFalse();
        tracker.TotalCompletionTokens.Should().Be(50);
    }

    private static SessionState MakeConversationSession()
    {
        var session = new SessionState();
        session.AddMessage(new Message { Role = MessageRole.System, Content = "System prompt" });
        session.AddMessage(new Message { Role = MessageRole.User, Content = "Hello there" });
        session.AddMessage(new Message
        {
            Role = MessageRole.Assistant,
            Content = "I can help with that.\n\n```csharp\nvar x = 42;\n```"
        });
        session.TurnCount = 1;
        return session;
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
            var chunks = _roundIndex < _rounds.Count
                ? _rounds[_roundIndex]
                : [new StreamChunk { TextDelta = "", IsComplete = true }];
            _roundIndex++;

            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                yield return chunk;
                await Task.Yield();
            }
        }

        public void Dispose() { }
    }
}

