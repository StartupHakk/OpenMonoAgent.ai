using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Tools;

namespace OpenMono.Tests.Integration;

public class CapabilityAndDeferredToolTests
{
    private readonly AppConfig _config;
    private readonly TerminalRenderer _renderer;
    private readonly PermissionEngine _permissions;

    public CapabilityAndDeferredToolTests()
    {
        _config = new AppConfig
        {
            WorkingDirectory = Path.GetTempPath(),
            DataDirectory = Path.Combine(Path.GetTempPath(), "openmono-test"),
            Permissions = new PermissionConfig(),
        };
        _renderer = new TerminalRenderer();
        _permissions = new PermissionEngine(_config, _renderer, _renderer);
    }

    [Fact]
    public void BuildToolDefinitions_ExcludesDeferredTools()
    {
        var registry = new ToolRegistry();
        registry.Register(new ActiveTestTool());
        registry.Register(new DeferredTestTool());

        var definitions = registry.BuildToolDefinitions();
        var json = definitions.GetRawText();

        json.Should().Contain("ActiveTestTool");
        json.Should().NotContain("DeferredTestTool");
    }

    [Fact]
    public void DeferredTools_Property_FiltersCorrectly()
    {
        var registry = new ToolRegistry();
        registry.Register(new ActiveTestTool());
        registry.Register(new DeferredTestTool());
        registry.Register(new AnotherDeferredTool());

        registry.DeferredTools.Should().HaveCount(2);
        registry.ActiveTools.Should().HaveCount(1);
    }

    [Fact]
    public void ToolSearchTool_IsNeverDeferred()
    {
        var toolSearch = new ToolSearchTool();
        toolSearch.IsDeferred.Should().BeFalse();
    }

    [Fact]
    public void ListDeferredTools_ReturnsCorrectList()
    {
        var registry = new ToolRegistry();
        registry.Register(new ActiveTestTool());
        registry.Register(new DeferredTestTool());

        var deferred = registry.ListDeferredTools();

        deferred.Should().HaveCount(1);
        deferred[0].Name.Should().Be("DeferredTestTool");
    }

    [Fact]
    public void SearchTools_FindsDeferredTools()
    {
        var registry = new ToolRegistry();
        registry.Register(new ActiveTestTool());
        registry.Register(new DeferredTestTool());

        var results = registry.SearchTools("Deferred", includeActive: false);

        results.Should().HaveCount(1);
        results[0].Name.Should().Be("DeferredTestTool");
    }

    [Fact]
    public void BuildToolDefinitionsFor_ReturnsRequestedSchemas()
    {
        var registry = new ToolRegistry();
        registry.Register(new ActiveTestTool());
        registry.Register(new DeferredTestTool());

        var definitions = registry.BuildToolDefinitionsFor(["DeferredTestTool"]);
        var json = definitions.GetRawText();

        json.Should().Contain("DeferredTestTool");
        json.Should().NotContain("ActiveTestTool");
    }

    [Fact]
    public async Task CheckCapabilities_EmptyCapabilities_AutoAllows()
    {
        var caps = new List<Capability>();

        var decision = await _permissions.CheckCapabilitiesAsync("TestTool", caps, CancellationToken.None);

        decision.Allowed.Should().BeTrue();
        decision.EvaluatedCapabilities.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckCapabilities_FileReadInWorkDir_AutoAllows()
    {
        var workDir = _config.WorkingDirectory;
        var caps = new List<Capability> { new FileReadCap(Path.Combine(workDir, "test.txt")) };

        var decision = await _permissions.CheckCapabilitiesAsync("FileRead", caps, CancellationToken.None);

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task CheckCapabilities_ProtectedPath_Denies()
    {
        var caps = new List<Capability> { new FileWriteCap("/etc/passwd", "modify") };

        var decision = await _permissions.CheckCapabilitiesAsync("FileWrite", caps, CancellationToken.None);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("Protected path");
    }

    [Fact]
    public async Task CheckCapabilities_BlockedBinary_Denies()
    {
        var caps = new List<Capability> { new ProcessExecCap("sudo", ["rm", "-rf", "/"]) };

        var decision = await _permissions.CheckCapabilitiesAsync("Bash", caps, CancellationToken.None);

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("Blocked binary");
    }

    [Fact]
    public async Task CheckCapabilities_SafeReadOnlyCommand_AutoAllows()
    {
        var caps = new List<Capability> { new ProcessExecCap("ls", ["-la"]) };

        var decision = await _permissions.CheckCapabilitiesAsync("Bash", caps, CancellationToken.None);

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public void DeferredTool_StillDeclaresCapabilities()
    {
        var deferredTool = new DeferredTestTool();
        var input = JsonDocument.Parse("""{"path": "/test/file.txt"}""").RootElement;

        var caps = deferredTool.RequiredCapabilities(input);

        caps.Should().HaveCount(1);
        caps[0].Should().BeOfType<FileReadCap>();
    }

    [Fact]
    public async Task DeferredTool_GoesthroughCapabilityCheck()
    {

        var registry = new ToolRegistry();
        var deferredTool = new DeferredTestTool();
        registry.Register(deferredTool);

        var schemas = registry.BuildToolDefinitionsFor(["DeferredTestTool"]);
        schemas.GetArrayLength().Should().Be(1);

        var input = JsonDocument.Parse($$"""{"path": "{{_config.WorkingDirectory}}/test.txt"}""").RootElement;
        var caps = deferredTool.RequiredCapabilities(input);

        var decision = await _permissions.CheckCapabilitiesAsync(deferredTool.Name, caps, CancellationToken.None);

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task ToolSearch_RequiresNoCapabilities()
    {
        var toolSearch = new ToolSearchTool();
        var input = JsonDocument.Parse("""{"list_deferred": true}""").RootElement;

        var caps = toolSearch.RequiredCapabilities(input);

        caps.Should().BeEmpty();

        var decision = await _permissions.CheckCapabilitiesAsync(toolSearch.Name, caps, CancellationToken.None);
        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public void FileReadCap_Summary_IsHumanReadable()
    {
        var cap = new FileReadCap("/path/to/file.txt");
        cap.Summary.Should().Be("Read file: /path/to/file.txt");
    }

    [Fact]
    public void FileWriteCap_Summary_ReflectsOperation()
    {
        new FileWriteCap("/file", "create").Summary.Should().Contain("Create");
        new FileWriteCap("/file", "modify").Summary.Should().Contain("Modify");
        new FileWriteCap("/file", "delete").Summary.Should().Contain("Delete");
    }

    [Fact]
    public void ProcessExecCap_FromCommand_ParsesCorrectly()
    {
        var cap = ProcessExecCap.FromCommand("git status --porcelain");

        cap.Binary.Should().Be("git");
        cap.Args.Should().Contain("status");
    }

    [Fact]
    public void NetworkEgressCap_FromUrl_ParsesCorrectly()
    {
        var cap = NetworkEgressCap.FromUrl("https://api.example.com:8443/v1/data");

        cap.Host.Should().Be("api.example.com");
        cap.Port.Should().Be(8443);
        cap.Protocol.Should().Be("https");
    }

    [Fact]
    public void VcsMutationCap_Summary_ShowsOperation()
    {
        var cap = new VcsMutationCap(".", "push");
        cap.Summary.Should().Contain("push");
    }

    [Fact]
    public void AgentSpawnCap_Summary_TruncatesLongTask()
    {
        var longTask = new string('x', 100);
        var cap = new AgentSpawnCap("Explore", longTask);

        cap.Summary.Length.Should().BeLessThan(100);
    }

    private class ActiveTestTool : ITool
    {
        public string Name => "ActiveTestTool";
        public string Description => "A test tool that is always active";
        public bool IsConcurrencySafe => true;
        public bool IsReadOnly => true;
        public bool IsDeferred => false;

        public JsonElement InputSchema { get; } = JsonDocument.Parse("{}").RootElement;

        public PermissionLevel RequiredPermission(JsonElement input) => PermissionLevel.AutoAllow;
        public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input) => [];

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct)
            => Task.FromResult(ToolResult.Success("ok"));
    }

    private class DeferredTestTool : ITool
    {
        public string Name => "DeferredTestTool";
        public string Description => "A test tool that is deferred";
        public bool IsConcurrencySafe => true;
        public bool IsReadOnly => true;
        public bool IsDeferred => true;

        public JsonElement InputSchema { get; } = JsonDocument.Parse("""
            {"type":"object","properties":{"path":{"type":"string"}}}
            """).RootElement;

        public PermissionLevel RequiredPermission(JsonElement input) => PermissionLevel.AutoAllow;

        public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input)
        {
            var path = input.TryGetProperty("path", out var p) ? p.GetString() : "/default";
            return [new FileReadCap(path ?? "/default")];
        }

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct)
            => Task.FromResult(ToolResult.Success("deferred tool executed"));
    }

    private class AnotherDeferredTool : ITool
    {
        public string Name => "AnotherDeferredTool";
        public string Description => "Another deferred tool";
        public bool IsConcurrencySafe => true;
        public bool IsReadOnly => false;
        public bool IsDeferred => true;

        public JsonElement InputSchema { get; } = JsonDocument.Parse("{}").RootElement;

        public PermissionLevel RequiredPermission(JsonElement input) => PermissionLevel.Ask;
        public IReadOnlyList<Capability> RequiredCapabilities(JsonElement input) => [];

        public Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct)
            => Task.FromResult(ToolResult.Success("ok"));
    }
}
