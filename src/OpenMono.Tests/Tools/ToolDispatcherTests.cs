using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Tools;

public class ToolDispatcherTests : IDisposable
{
    private readonly string _tempDir;

    public ToolDispatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-disp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task ReadOnlyTool_ExceedingItsTimeout_ReturnsErrorWithoutHanging()
    {
        var tool = new SlowTool(timeout: TimeSpan.FromMilliseconds(50), work: TimeSpan.FromSeconds(30));
        using var dispatcher = MakeDispatcher(maxReadOnly: 4, tool);
        var calls = new List<ToolCall> { new() { Id = "1", Name = tool.Name, Arguments = "{}" } };

        var results = await dispatcher.ExecuteToolCallsAsync(calls, CancellationToken.None);

        results[0].IsError.Should().BeTrue();
        results[0].Content.Should().Contain("timed out");
    }

    [Fact]
    public async Task ReadOnlyTools_RunNoMoreThanTheConcurrencyCapAtOnce()
    {
        var probe = new ConcurrencyProbe();
        var tool = new ConcurrentTool(probe);
        using var dispatcher = MakeDispatcher(maxReadOnly: 2, tool);

        // Six distinct calls (distinct args so the doom-loop guard doesn't trip).
        var calls = Enumerable.Range(0, 6)
            .Select(i => new ToolCall { Id = i.ToString(), Name = tool.Name, Arguments = $"{{\"i\":{i}}}" })
            .ToList();

        var results = await dispatcher.ExecuteToolCallsAsync(calls, CancellationToken.None);

        results.Should().OnlyContain(r => !r.IsError);
        probe.Peak.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task PreToolUseHook_ExitingWithCode2_BlocksTheTool()
    {
        var tool = new FlagTool();
        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        config.Hooks.PreToolUse.Add(new HookDefinition { Run = "exit 2" });

        var registry = new ToolRegistry();
        registry.Register(tool);
        var renderer = new TerminalRenderer();
        var permissions = new PermissionEngine(config, renderer, renderer);
        using var dispatcher = new ToolDispatcher(registry, permissions, renderer, config, new SessionState());

        var results = await dispatcher.ExecuteToolCallsAsync(
            new List<ToolCall> { new() { Id = "1", Name = tool.Name, Arguments = "{}" } },
            CancellationToken.None);

        results[0].IsError.Should().BeTrue();
        tool.Executed.Should().BeFalse("a PreToolUse hook exiting 2 must block the tool, not just warn");
    }

    private ToolDispatcher MakeDispatcher(int maxReadOnly, params ITool[] tools)
    {
        var registry = new ToolRegistry();
        foreach (var t in tools) registry.Register(t);

        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        var renderer = new TerminalRenderer();
        var permissions = new PermissionEngine(config, renderer, renderer);

        return new ToolDispatcher(
            registry, permissions, renderer, config, new SessionState(),
            maxReadOnlyConcurrency: maxReadOnly);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private sealed class SlowTool : ToolBase
    {
        private readonly TimeSpan _work;
        public SlowTool(TimeSpan timeout, TimeSpan work) { Timeout = timeout; _work = work; }

        public override string Name => "SlowRead";
        public override string Description => "test";
        public override bool IsReadOnly => true;
        public override bool IsConcurrencySafe => true;
        public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;
        public override TimeSpan? Timeout { get; }

        protected override SchemaBuilder DefineSchema() => new();

        protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
        {
            await Task.Delay(_work, ct);
            return ToolResult.Success("done");
        }
    }

    private sealed class ConcurrentTool : ToolBase
    {
        private readonly ConcurrencyProbe _probe;
        public ConcurrentTool(ConcurrencyProbe probe) => _probe = probe;

        public override string Name => "ConcRead";
        public override string Description => "test";
        public override bool IsReadOnly => true;
        public override bool IsConcurrencySafe => true;
        public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

        protected override SchemaBuilder DefineSchema() => new SchemaBuilder().AddInteger("i", "index");

        protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
        {
            _probe.Enter();
            try { await Task.Delay(100, ct); }
            finally { _probe.Exit(); }
            return ToolResult.Success("ok");
        }
    }

    private sealed class FlagTool : ToolBase
    {
        public bool Executed { get; private set; }
        public override string Name => "FlagTool";
        public override string Description => "test";
        public override bool IsReadOnly => true;
        public override bool IsConcurrencySafe => true;
        public override PermissionLevel DefaultPermission => PermissionLevel.AutoAllow;

        protected override SchemaBuilder DefineSchema() => new();

        protected override Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
        {
            Executed = true;
            return Task.FromResult(ToolResult.Success("ran"));
        }
    }

    private sealed class ConcurrencyProbe
    {
        private int _current;
        private int _peak;

        public int Peak => Volatile.Read(ref _peak);

        public void Enter()
        {
            var cur = Interlocked.Increment(ref _current);
            int observed;
            while (cur > (observed = Volatile.Read(ref _peak)))
                Interlocked.CompareExchange(ref _peak, cur, observed);
        }

        public void Exit() => Interlocked.Decrement(ref _current);
    }
}
