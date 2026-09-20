using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Decisions;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Decisions;

public class PhaseTwoBrittlenessTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DecisionOptions _options = new(false, 0.85, 0.6, 0.5, 64);

    public PhaseTwoBrittlenessTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void JudgeTrue_KnotDoesNotFlip()
    {
        var backend = new HeuristicBackend(_options);

        backend.JudgeTrue("a knot tied here in the rope", "a knot tied here").Should().BeGreaterThan(0.5);
    }

    [Fact]
    public void JudgeTrue_NotedDoesNotFlip()
    {
        var backend = new HeuristicBackend(_options);

        backend.JudgeTrue("the events as noted in the log", "events as noted").Should().BeGreaterThan(0.5);
    }

    [Fact]
    public void JudgeTrue_RealNegationStillFlips()
    {
        var backend = new HeuristicBackend(_options);

        backend.JudgeTrue("the task is complete and shipped", "the task is not complete")
            .Should().BeLessThan(0.5);
    }

    [Fact]
    public void JudgeTrue_ContractionNegationStillFlips()
    {
        var backend = new HeuristicBackend(_options);

        backend.JudgeTrue("the deploy finished cleanly", "the deploy didn't finish")
            .Should().BeGreaterThan(0.5);
    }

    [Fact]
    public void Verify_KnotEvidenceIsNotContradiction()
    {
        var backend = new HeuristicBackend(_options);

        var (verdict, _) = backend.Verify(
            "Tie a knot with this rope and pull it tight.",
            "knot with rope");

        verdict.Should().Be("supported");
    }

    [Fact]
    public async Task Router_UndoneIsNotDoneMarker()
    {
        var router = new ChiefRouter(_options, _tempDir);

        var (handoff, _) = await router.RouteAsync(
            "Fix login", "The login fix is undone and reverted, no sources yet", null, CancellationToken.None);

        handoff.Destination.Should().Be("research");
    }

    [Fact]
    public async Task Router_NotDoneHitsDoneMarkerConservatively()
    {
        var router = new ChiefRouter(_options, _tempDir);

        var (handoff, _) = await router.RouteAsync(
            "Fix login", "I'm not done with the login fix", null, CancellationToken.None);

        handoff.Destination.Should().Be("review");
    }

    [Fact]
    public async Task Router_NonEnglishAbstainsToReview()
    {
        var router = new ChiefRouter(_options, _tempDir);

        var (handoff, _) = await router.RouteAsync(
            "比较三个智能体工具", "已经收集了证据和来源并起草了初稿", null, CancellationToken.None);

        handoff.Destination.Should().Be("review");
        handoff.Confidence.Should().Be(0.5);
    }

    [Fact]
    public async Task Evaluate_OversizedStateSurfacesTruncatedFlag()
    {
        var tool = new DecideEvaluateTool(_options);
        var blob = new string('e', DecisionCaps.MaxStateChars + 100);
        var input = JsonDocument.Parse(
            $$"""{"state": {"blob": "{{blob}}"}, "questions": {"q1": {"type": "noul", "instructions": "still relevant?" } } }""").RootElement;

        var result = await tool.ExecuteAsync(input, TestContext(), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("\"truncated\":true");
    }

    [Fact]
    public void Gate_SignalsComeFromSingleParse()
    {
        var gate = new DecisionGateTool(_options, _tempDir);

        var inScope = Path.Combine(_tempDir, "notes.txt");
        File.WriteAllText(inScope, "scratch");
        var allowed = gate.Check("Bash", $$"""{"command": "rm {{inScope}}"}""", "clean scratch");
        allowed.Decision.Should().Be("allow");
        allowed.Signals.InScope.Should().BeTrue();

        var confirm = gate.Check("FileWrite", """{"file_path": "/etc/passwd"}""", "save draft");
        confirm.Decision.Should().Be("confirm");
        confirm.Signals.InScope.Should().BeFalse();
    }

    [Fact]
    public void Gate_UnparseableArgsConfirmWithoutThrowing()
    {
        var gate = new DecisionGateTool(_options, _tempDir);

        var result = gate.Check("Bash", """{"command": }""", "do it");

        result.Decision.Should().Be("confirm");
        result.Reasons.Should().Contain("unparseable-args");
    }

    [Fact]
    public async Task Audit_AppendFailureReturnsFalse()
    {
        var blocker = Path.Combine(_tempDir, "blocker");
        File.WriteAllText(blocker, "i am a file, not a directory");
        var audit = new DecisionAudit(new AppConfig { DataDirectory = blocker });

        var ok = await audit.AppendAsync(new DecisionAudit.Entry(
            DateTime.UtcNow.ToString("o"), "sess-1", "gate", "test", 0));

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task Gate_AuditFailureSurfacedInPayloadWithoutBlocking()
    {
        var blocker = Path.Combine(_tempDir, "blocker");
        File.WriteAllText(blocker, "i am a file, not a directory");
        var audit = new DecisionAudit(new AppConfig { DataDirectory = blocker });
        var gate = new DecisionGateTool(_options, _tempDir, audit: audit);
        var input = JsonDocument.Parse(
            """{"tool": "Bash", "args": {"command": "ls"}, "user_request": "list files"}""").RootElement;

        var result = await gate.ExecuteAsync(input, TestContext(), CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("\"audit\":\"failed\"");
        result.Content.Should().Contain("\"decision\":\"allow\"");
    }

    [Fact]
    public async Task FindByHash_IndexScalesToTwoHundredFiles()
    {
        const string dest = "research";
        string? wanted = null;
        for (var i = 0; i < 200; i++)
        {
            var handoff = new JobHandoff
            {
                Goal = $"goal {i}",
                CompletedWork = $"work {i}",
                Choice = "research",
                Confidence = 0.9,
                Destination = dest,
                Status = "queued",
                Model = "local-heuristic",
                AutoThreshold = 0.85,
                ReviewThreshold = 0.6,
                Timestamp = DateTime.UtcNow.ToString("o"),
                Uuid = $"perf-{i:000}",
                ContentHash = JobHandoff.ComputeHash($"goal {i}", $"work {i}", dest),
                Capped = false,
            };
            await handoff.SaveAsync(_tempDir, CancellationToken.None);
            if (i == 199)
                wanted = handoff.ContentHash;
        }

        var sw = Stopwatch.StartNew();
        var found = JobHandoff.FindByHash(_tempDir, dest, wanted!);
        sw.Stop();

        found.Should().NotBeNull();
        found!.Uuid.Should().Be("perf-199");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void FindByHash_FallsBackToScanForLegacyFiles()
    {
        const string dest = "write";
        var dir = Path.Combine(_tempDir, ".openmono", "decision-queue", dest);
        Directory.CreateDirectory(dir);
        var hash = JobHandoff.ComputeHash("legacy goal", "legacy work", dest);
        File.WriteAllText(Path.Combine(dir, "legacy.json"), $$"""
            {"goal":"legacy goal","completed_work":"legacy work","choice":"write","confidence":0.9,
             "destination":"write","status":"queued","model":"local-heuristic","auto_threshold":0.85,
             "review_threshold":0.6,"timestamp":"2026-01-01T00:00:00Z","uuid":"legacy",
             "content_hash":"{{hash}}","capped":false}
            """);

        var found = JobHandoff.FindByHash(_tempDir, dest, hash);

        found.Should().NotBeNull();
        found!.Uuid.Should().Be("legacy");
        JobHandoff.FindByHash(_tempDir, dest, "deadbeefdeadbeef").Should().BeNull();
    }

    private ToolContext TestContext()
    {
        var config = new AppConfig { WorkingDirectory = _tempDir, DataDirectory = _tempDir };
        var renderer = new TerminalRenderer();
        return new ToolContext
        {
            ToolRegistry = new ToolRegistry(),
            Session = new SessionState(),
            Permissions = new PermissionEngine(config, renderer, renderer),
            Config = config,
            WorkingDirectory = _tempDir,
            WriteOutput = _ => { },
            AskUser = (_, _) => Task.FromResult(""),
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
        GC.SuppressFinalize(this);
    }
}
