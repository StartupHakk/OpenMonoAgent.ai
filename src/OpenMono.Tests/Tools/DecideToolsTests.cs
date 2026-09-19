using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using OpenMono.Decisions;
using OpenMono.Permissions;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;

namespace OpenMono.Tests.Tools;

public class DecideToolsTests : IDisposable
{
    private readonly DecisionOptions _options = new(true, 0.85, 0.6, 0.5, 64);
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");

    private ToolContext Context(string workdir) => new()
    {
        ToolRegistry = new ToolRegistry(),
        Session = new SessionState(),
        Permissions = new PermissionEngine(new AppConfig(), new TerminalRenderer(), new TerminalRenderer()),
        Config = new AppConfig { WorkingDirectory = workdir, DataDirectory = _tempDir },
        WorkingDirectory = workdir,
        WriteOutput = _ => { },
        AskUser = (_, _) => Task.FromResult(""),
    };

    [Fact]
    public async Task Evaluate_ChoiceReturnsProbabilitiesAndGate()
    {
        var tool = new DecideEvaluateTool(_options);
        var input = JsonDocument.Parse("""
            {
                "state": {"goal": "Compare tools", "completed_work": "No sources, no findings yet"},
                "questions": {
                    "next": {
                        "type": "choice",
                        "instructions": "Choose the next step.",
                        "criteria": {"research": "Collect evidence", "write": "Draft briefing"}
                    }
                }
            }
            """).RootElement;

        var result = await tool.ExecuteAsync(input, Context(Path.GetTempPath()), CancellationToken.None);
        var doc = JsonDocument.Parse(result.Content);

        result.IsError.Should().BeFalse();
        doc.RootElement.GetProperty("answers").GetProperty("next").GetProperty("choice").GetString()
            .Should().Be("research");
        doc.RootElement.TryGetProperty("gate", out _).Should().BeTrue();
        doc.RootElement.TryGetProperty("latency_ms", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_NoulAndScoreAnswerTypes()
    {
        var tool = new DecideEvaluateTool(_options);
        var input = JsonDocument.Parse("""
            {
                "state": {"text": "Payouts failing for three days, urgent outage"},
                "questions": {
                    "urgent": {"type": "noul", "instructions": "Does this convey urgency?"},
                    "relevance": {"type": "score", "instructions": "Rate relevance.", "criteria": ["low", "high"]}
                }
            }
            """).RootElement;

        var result = await tool.ExecuteAsync(input, Context(Path.GetTempPath()), CancellationToken.None);
        var doc = JsonDocument.Parse(result.Content);

        doc.RootElement.GetProperty("answers").GetProperty("urgent").TryGetProperty("noul", out _).Should().BeTrue();
        doc.RootElement.GetProperty("answers").GetProperty("relevance").TryGetProperty("score", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Evaluate_RejectsOversizedInput()
    {
        var tool = new DecideEvaluateTool(_options);
        var input = JsonDocument.Parse(
            $"{{\"state\": {{\"blob\": \"{new string('x', DecideEvaluateTool.MaxInputChars + 1)}\"}}, \"questions\": {{}}}}").RootElement;

        var result = await tool.ExecuteAsync(input, Context(Path.GetTempPath()), CancellationToken.None);

        result.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Rank_OrdersByRelevanceAndFiltersTail()
    {
        var tool = new DecideRankTool(_options);
        var input = JsonDocument.Parse("""
            {
                "query": "phone number",
                "candidates": [
                    {"id": "a", "text": "database migration checklist"},
                    {"id": "b", "text": "Phone number field"},
                    {"id": "c", "text": "phone"}
                ],
                "min_relevance": 0.5
            }
            """).RootElement;

        var result = await tool.ExecuteAsync(input, Context(Path.GetTempPath()), CancellationToken.None);
        var doc = JsonDocument.Parse(result.Content);
        var ranked = doc.RootElement.GetProperty("ranked").EnumerateArray().ToList();

        ranked[0].GetProperty("id").GetString().Should().Be("b");
        ranked[1].GetProperty("id").GetString().Should().Be("c");
        ranked.Should().HaveCount(2);
        doc.RootElement.GetProperty("any_relevant").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Verify_SupportsCoveredClaimsOnly()
    {
        var tool = new DecideVerifyTool(_options);
        var input = JsonDocument.Parse("""
            {
                "evidence": "The database migration completed successfully on all three nodes.",
                "claims": ["migration completed", "lunar landing schedule"]
            }
            """).RootElement;

        var result = await tool.ExecuteAsync(input, Context(Path.GetTempPath()), CancellationToken.None);
        var doc = JsonDocument.Parse(result.Content);
        var verdicts = doc.RootElement.GetProperty("verdicts").EnumerateArray().ToList();

        verdicts[0].GetProperty("verdict").GetString().Should().Be("supported");
        verdicts[1].GetProperty("verdict").GetString().Should().Be("not_addressed");
        doc.RootElement.GetProperty("all_supported").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task NextStep_RetriesOnEmptyResult()
    {
        var tool = new DecideNextStepTool(_options);
        var input = JsonDocument.Parse("""
            {"goal": "Deploy service", "last_action": "run deploy", "result": "", "attempts": 1}
            """).RootElement;

        var result = await tool.ExecuteAsync(input, Context(Path.GetTempPath()), CancellationToken.None);

        JsonDocument.Parse(result.Content).RootElement.GetProperty("verdict").GetString().Should().Be("retry");
    }

    [Fact]
    public async Task NextStep_EscalatesAfterThreeAttempts()
    {
        var tool = new DecideNextStepTool(_options);
        var input = JsonDocument.Parse("""
            {"goal": "Deploy service", "last_action": "run deploy", "result": "still failing", "attempts": 4}
            """).RootElement;

        var result = await tool.ExecuteAsync(input, Context(Path.GetTempPath()), CancellationToken.None);
        var verdict = JsonDocument.Parse(result.Content).RootElement.GetProperty("verdict").GetString();

        verdict.Should().BeOneOf("change_approach", "ask_user");
    }

    [Fact]
    public async Task NextStep_CompletesOnStrongEvidence()
    {
        var tool = new DecideNextStepTool(_options);
        var input = JsonDocument.Parse("""
            {"goal": "Deploy service", "last_action": "run deploy",
             "result": "Deploy service completed successfully. Deploy service completed successfully.", "attempts": 1}
            """).RootElement;

        var result = await tool.ExecuteAsync(input, Context(Path.GetTempPath()), CancellationToken.None);

        JsonDocument.Parse(result.Content).RootElement.GetProperty("verdict").GetString().Should().Be("done");
    }

    [Fact]
    public async Task Rank_WritesAuditLine()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        var tool = new DecideRankTool(_options);
        var context = new ToolContext
        {
            ToolRegistry = new ToolRegistry(),
            Session = new SessionState(),
            Permissions = new PermissionEngine(new AppConfig(), new TerminalRenderer(), new TerminalRenderer()),
            Config = new AppConfig { WorkingDirectory = Path.GetTempPath(), DataDirectory = dataDir },
            WorkingDirectory = Path.GetTempPath(),
            WriteOutput = _ => { },
            AskUser = (_, _) => Task.FromResult(""),
        };
        var input = JsonDocument.Parse("""
            {"query": "phone", "candidates": [{"id": "a", "text": "phone field"}]}
            """).RootElement;

        await tool.ExecuteAsync(input, context, CancellationToken.None);

        var lines = await File.ReadAllLinesAsync(Path.Combine(dataDir, "decision-audit.jsonl"));
        lines.Should().HaveCount(1);
        lines[0].Should().Contain("rank");
        Directory.Delete(dataDir, true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task NextStep_ContinuesOnPartialProgress()
    {
        var tool = new DecideNextStepTool(_options);
        var input = JsonDocument.Parse("""
            {"goal": "Deploy service", "last_action": "run tests", "result": "Working on it, partial progress", "attempts": 1}
            """).RootElement;

        var result = await tool.ExecuteAsync(input, Context(Path.GetTempPath()), CancellationToken.None);

        JsonDocument.Parse(result.Content).RootElement.GetProperty("verdict").GetString().Should().Be("continue");
    }
}
