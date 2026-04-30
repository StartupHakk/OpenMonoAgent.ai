using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Tools;

public class BashToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly BashTool _tool;
    private readonly ToolContext _context;

    public BashToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _tool = new BashTool();
        _context = CreateContext(_tempDir);
    }

    [Fact]
    public async Task ExecuteSimpleCommand_ReturnsOutput()
    {
        var input = JsonDocument.Parse("""{"command": "echo hello"}""").RootElement;
        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("hello");
    }

    [Fact]
    public async Task FailingCommand_ReturnsExitCode()
    {
        var input = JsonDocument.Parse("""{"command": "exit 42"}""").RootElement;
        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);

        result.Content.Should().Contain("42");
    }

    [Fact]
    public void Permission_IsAsk()
    {
        var input = JsonDocument.Parse("""{"command": "echo test"}""").RootElement;
        _tool.RequiredPermission(input).Should().Be(PermissionLevel.Ask);
    }

    [Fact]
    public void IsNotReadOnly()
    {
        _tool.IsReadOnly.Should().BeFalse();
    }

    [Fact]
    public async Task TimeoutKillsProcess_ReturnsTimeoutErrorPromptly()
    {

        var input = JsonDocument.Parse("""{"command": "sleep 5", "timeout_ms": 500}""").RootElement;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);
        sw.Stop();

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("timed out");

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task TimeoutKillsEntireProcessTree_NoOrphanedDescendants()
    {

        var pidFile = Path.Combine(_tempDir, "child.pid");
        var script = $"sleep 30 & echo $! > {pidFile}; wait";

        var input = JsonDocument.Parse(
            $$"""{"command": "{{script}}", "timeout_ms": 500}""").RootElement;

        var result = await _tool.ExecuteAsync(input, _context, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("timed out");

        await Task.Delay(500);

        if (File.Exists(pidFile))
        {
            var pidText = (await File.ReadAllTextAsync(pidFile)).Trim();
            if (int.TryParse(pidText, out var childPid))
            {
                var stillAlive = ProcessIsAlive(childPid);
                stillAlive.Should().BeFalse(
                    $"child sleep PID {childPid} should have been killed with the process tree");
            }
        }
    }

    [Fact]
    public async Task UserCancellation_KillsProcess()
    {
        var input = JsonDocument.Parse("""{"command": "sleep 30"}""").RootElement;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _tool.ExecuteAsync(input, _context, cts.Token);
        sw.Stop();

        result.IsError.Should().BeTrue();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
    }

    private static bool ProcessIsAlive(int pid)
    {
        try
        {
            var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static ToolContext CreateContext(string workDir) => new()
    {
        ToolRegistry = new ToolRegistry(),
        Session = new SessionState(),
        Permissions = new PermissionEngine(new AppConfig(), new TerminalRenderer(), new TerminalRenderer()),
        Config = new AppConfig { WorkingDirectory = workDir },
        WorkingDirectory = workDir,
        WriteOutput = _ => { },
        AskUser = (_, _) => Task.FromResult(""),
    };
}
