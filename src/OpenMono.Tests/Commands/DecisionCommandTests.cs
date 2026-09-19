using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Commands;
using OpenMono.Config;
using OpenMono.Decisions;
using OpenMono.Llm;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Commands;

public class DecisionCommandTests : IDisposable
{
    private readonly string _tempDir;

    public DecisionCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void StatusText_ReportsThresholdsAndDisabledByDefault()
    {
        var text = DecisionCommand.StatusText(new DecisionSettings());

        text.Should().Contain("disabled");
        text.Should().Contain("auto_threshold=0.85");
        text.Should().Contain("max_steps=64");
    }

    [Fact]
    public void Tokenize_SplitsRespectingQuotes()
    {
        DecisionCommand.Tokenize("chief --goal \"Compare tools\" --notes \"No sources yet\"")
            .Should().Equal("chief", "--goal", "Compare tools", "--notes", "No sources yet");
    }

    [Fact]
    public async Task Chief_AcceptsSingleRawArg()
    {
        var command = new DecisionCommand();
        var context = Context();

        await command.ExecuteAsync(
            ["chief --goal \"Compare tools\" --notes \"No sources yet\""], context, CancellationToken.None);

        Directory.EnumerateFiles(
            Path.Combine(_tempDir, ".openmono", "decision-queue", "research"), "*.json").Should().HaveCount(1);
    }

    [Fact]
    public void ParseFlags_CollectsMultiWordValues()
    {
        var flags = DecisionCommand.ParseFlags(["--goal", "Compare", "three", "tools", "--notes", "None"]);

        flags["goal"].Should().Be("Compare three tools");
        flags["notes"].Should().Be("None");
    }

    [Fact]
    public async Task Chief_WritesQueueHandoff()
    {
        var command = new DecisionCommand();
        var context = Context();

        await command.ExecuteAsync(
            ["chief", "--goal", "Compare tools", "--notes", "No sources yet"], context, CancellationToken.None);

        var files = Directory.EnumerateFiles(
            Path.Combine(_tempDir, ".openmono", "decision-queue", "research"), "*.json").ToList();
        files.Should().HaveCount(1);
        JobHandoff.Load(files[0])!.Destination.Should().Be("research");
    }

    [Fact]
    public async Task Status_RunsWithoutError()
    {
        var command = new DecisionCommand();

        await command.ExecuteAsync(["status"], Context(), CancellationToken.None);
    }

    private CommandContext Context() => new()
    {
        Session = new SessionState(),
        ToolRegistry = new ToolRegistry(),
        CommandRegistry = new CommandRegistry(),
        Config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir },
        Renderer = new TerminalRenderer(),
        WorkingDirectory = _tempDir,
        Llm = new CompleteLlmClient(),
    };

    private sealed class CompleteLlmClient : ILlmClient
    {
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages,
            JsonElement? tools,
            LlmOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new StreamChunk { IsComplete = true };
            await Task.CompletedTask;
        }

        public void Dispose() { }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
        GC.SuppressFinalize(this);
    }
}
