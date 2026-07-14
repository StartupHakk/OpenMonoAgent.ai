using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Llm;
using OpenMono.Permissions;
using OpenMono.Playbooks;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Playbooks;

public class PlaybookExecutorTests : IDisposable
{
    private readonly string _tempDir;

    public PlaybookExecutorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-pb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task ExecuteAsync_SavesStateUnderSessionId_SoResumeCanFindIt()
    {
        const string sessionId = "sess1234abcd";
        var playbook = new PlaybookDefinition
        {
            Name = "demo",
            Description = "demo playbook",
            Steps = [new StepDefinition { Id = "step1", InlinePrompt = "do the thing" }],
        };

        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        var renderer = new TerminalRenderer();
        var permissions = new PermissionEngine(config, renderer, renderer);
        using var executor = new PlaybookExecutor(
            new ImmediateLlmClient(), new ToolRegistry(), renderer, config, permissions);

        await executor.ExecuteAsync(
            playbook, new Dictionary<string, object>(), resumeFrom: null, sessionId, CancellationToken.None);

        // The state must be loadable with the SAME key the resume path uses (the chat session id).
        // Otherwise PlaybookState.LoadAsync(..., context.Session.Id) never matches and resume
        // silently restarts the whole playbook from step 1.
        var loaded = await PlaybookState.LoadAsync(
            config.DataDirectory, playbook.Name, sessionId, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.IsStepCompleted("step1").Should().BeTrue();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private sealed class ImmediateLlmClient : ILlmClient
    {
        public async IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages,
            JsonElement? tools,
            LlmOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new StreamChunk { TextDelta = "done", IsComplete = true };
            await Task.CompletedTask;
        }

        public void Dispose() { }
    }
}
