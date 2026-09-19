using FluentAssertions;
using OpenMono.Decisions;

namespace OpenMono.Tests.Decisions;

public class JobHandoffLifecycleTests : IDisposable
{
    private readonly string _tempDir;

    public JobHandoffLifecycleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    private static JobHandoff NewHandoff(string uuid, string choice, string dest, string timestamp) => new()
    {
        Goal = "Compare tools",
        CompletedWork = "No sources yet",
        Choice = choice,
        Confidence = 0.9,
        Destination = dest,
        Status = "queued",
        Model = "local-heuristic",
        AutoThreshold = 0.85,
        ReviewThreshold = 0.6,
        Timestamp = timestamp,
        Uuid = uuid,
        ContentHash = JobHandoff.ComputeHash("Compare tools", "No sources yet", dest),
        Capped = false,
    };

    [Fact]
    public async Task Save_Claim_Reclaim_Done_Lifecycle()
    {
        var path = await NewHandoff("w1", "research", "research", "2026-01-01T00:00:00Z").SaveAsync(_tempDir, CancellationToken.None);

        JobHandoff.Load(path)!.Status.Should().Be("queued");

        var claimed = JobHandoff.TryClaim(path);
        claimed.Should().NotBeNull();
        claimed!.Status.Should().Be("claimed");
        JobHandoff.Load(path)!.Status.Should().Be("claimed");

        var reclaimed = JobHandoff.TryClaim(path);
        reclaimed!.Status.Should().Be("claimed");

        var done = JobHandoff.MarkDone(path);
        done!.Status.Should().Be("done");
        JobHandoff.Load(path)!.Status.Should().Be("done");
    }

    [Fact]
    public void TryClaim_MissingFile_ReturnsNull()
    {
        JobHandoff.TryClaim(Path.Combine(_tempDir, "missing.json")).Should().BeNull();
        JobHandoff.MarkDone(Path.Combine(_tempDir, "missing.json")).Should().BeNull();
    }

    [Fact]
    public async Task MarkDone_FromQueued_GoesStraightToDone()
    {
        var path = await NewHandoff("w2", "write", "write", "2026-01-01T00:00:00Z").SaveAsync(_tempDir, CancellationToken.None);

        var done = JobHandoff.MarkDone(path);

        done!.Status.Should().Be("done");
        JobHandoff.TryClaim(path)!.Status.Should().Be("done");
    }

    [Fact]
    public async Task List_ReturnsNewestFirstSkipsCorruptAndCaps()
    {
        await NewHandoff("u1", "research", "research", "2026-01-01T00:00:00Z").SaveAsync(_tempDir, CancellationToken.None);
        await NewHandoff("u2", "research", "research", "2026-01-01T00:00:01Z").SaveAsync(_tempDir, CancellationToken.None);
        await NewHandoff("u3", "research", "research", "2026-01-01T00:00:02Z").SaveAsync(_tempDir, CancellationToken.None);
        File.WriteAllText(Path.Combine(_tempDir, ".openmono", "decision-queue", "research", "bad.json"), "not json {{{");

        var all = JobHandoff.List(_tempDir, "research");

        all.Select(h => h.Uuid).Should().Equal("u3", "u2", "u1");

        var capped = JobHandoff.List(_tempDir, "research", 2);

        capped.Select(h => h.Uuid).Should().Equal("u3", "u2");
        JobHandoff.List(_tempDir, "write").Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
        GC.SuppressFinalize(this);
    }
}
