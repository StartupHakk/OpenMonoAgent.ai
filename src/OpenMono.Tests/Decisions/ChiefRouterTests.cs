using FluentAssertions;
using OpenMono.Decisions;

namespace OpenMono.Tests.Decisions;

public class ChiefRouterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ChiefRouter _router;

    public ChiefRouterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _router = new ChiefRouter(new DecisionOptions(true, 0.85, 0.6, 0.5, 64), _tempDir);
    }

    [Fact]
    public async Task RouteAsync_NoSourcesRoutesToResearch()
    {
        var (handoff, path) = await _router.RouteAsync(
            "Compare three AI-agent tools", "No sources yet", null, CancellationToken.None);

        handoff.Choice.Should().Be("research");
        handoff.Destination.Should().Be("research");
        handoff.Status.Should().Be("queued");
        handoff.Confidence.Should().Be(0.9);
        path.Should().Contain("research");
        JobHandoff.Load(path).Should().NotBeNull();
    }

    [Fact]
    public async Task RouteAsync_EvidenceRoutesToWriteWhenThresholdAllows()
    {
        var (handoff, _) = await _router.RouteAsync(
            "Compare three AI-agent tools",
            "Sources collected. Evidence gathered. Findings summarized. Ready to draft.",
            0.6, CancellationToken.None);

        handoff.Choice.Should().Be("write");
        handoff.Destination.Should().Be("write");
    }

    [Fact]
    public async Task RouteAsync_MarginalEvidenceFallsBackToReviewByDefault()
    {
        var (handoff, _) = await _router.RouteAsync(
            "Compare three AI-agent tools",
            "Sources collected. Evidence gathered. Findings summarized. Ready to draft.",
            null, CancellationToken.None);

        handoff.Choice.Should().Be("write");
        handoff.Destination.Should().Be("review");
    }

    [Fact]
    public async Task RouteAsync_UnclearGoalRoutesToReview()
    {
        var (handoff, path) = await _router.RouteAsync(
            "Maybe compare tools?", "Goal unclear, out of scope ideas only", null, CancellationToken.None);

        handoff.Destination.Should().Be("review");
        path.Should().Contain("review");
    }

    [Fact]
    public async Task RouteAsync_CompletedWorkRoutesToReview()
    {
        var (handoff, _) = await _router.RouteAsync(
            "Compare tools", "Briefing complete and published", null, CancellationToken.None);

        handoff.Destination.Should().Be("review");
    }

    [Fact]
    public async Task RouteAsync_OversizedStateCapsToReview()
    {
        var (handoff, _) = await _router.RouteAsync(
            "Goal", new string('x', ChiefRouter.MaxStateChars + 1), null, CancellationToken.None);

        handoff.Destination.Should().Be("review");
        handoff.Capped.Should().BeTrue();
    }

    [Fact]
    public async Task RouteAsync_DeduplicatesIdenticalRequests()
    {
        var first = await _router.RouteAsync("Compare tools", "No sources yet", null, CancellationToken.None);
        var second = await _router.RouteAsync("Compare tools", "No sources yet", null, CancellationToken.None);

        second.Handoff.Uuid.Should().Be(first.Handoff.Uuid);
        Directory.EnumerateFiles(Path.Combine(_tempDir, ".openmono", "decision-queue", "research")).Should().HaveCount(1);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
        GC.SuppressFinalize(this);
    }
}
