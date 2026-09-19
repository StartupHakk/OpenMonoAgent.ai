using FluentAssertions;
using OpenMono.Config;
using OpenMono.Decisions;

namespace OpenMono.Tests.Decisions;

public class DecisionReportAuditTests
{
    [Fact]
    public void ToRedactedMarkdown_HidesSecretValues()
    {
        var report = new DecisionReport(
            "triage",
            true,
            "execute:false",
            [],
            [new DecisionItem("file", "a.cs", "act", 0.9, 0, "key=AKIAIOSFODNN7EXAMPLE")],
            [],
            new Dictionary<string, long>(),
            []);

        var markdown = report.ToRedactedMarkdown();

        markdown.Should().NotContain("AKIAIOSFODNN7EXAMPLE");
        markdown.Should().Contain("[REDACTED]");
        markdown.Should().Contain("a.cs");
    }

    [Fact]
    public void ToString_NeverEmitsDetails()
    {
        var report = new DecisionReport(
            "triage",
            true,
            "execute:false",
            [],
            [new DecisionItem("file", "a.cs", "act", 0.9, 0, "key=AKIAIOSFODNN7EXAMPLE")],
            [],
            new Dictionary<string, long>(),
            []);

        report.ToString().Should().NotContain("AKIA");
    }

    [Fact]
    public async Task AppendAsync_WritesOneJsonLine()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        var audit = new DecisionAudit(new AppConfig { DataDirectory = dataDir });

        await audit.AppendAsync(new DecisionAudit.Entry(
            DateTime.UtcNow.ToString("o"), "sess-1", "chief", "research 0.92", 3));

        var lines = await File.ReadAllLinesAsync(Path.Combine(dataDir, "decision-audit.jsonl"));
        lines.Should().HaveCount(1);
        lines[0].Should().Contain("sess-1").And.Contain("chief");
        Directory.Delete(dataDir, true);
    }

    [Fact]
    public async Task AppendAsync_NeverThrowsOnUnwritableDirectory()
    {
        var marker = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        File.WriteAllText(marker, "x");
        var audit = new DecisionAudit(new AppConfig { DataDirectory = marker });

        await audit.AppendAsync(new DecisionAudit.Entry(
            DateTime.UtcNow.ToString("o"), "sess-1", "chief", "research", 1));

        File.Delete(marker);
    }
}
