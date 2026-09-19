using FluentAssertions;
using OpenMono.Decisions;

namespace OpenMono.Tests.Decisions;

public class BenchReplayTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ChiefRouter _router;

    public BenchReplayTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _router = new ChiefRouter(new DecisionOptions(true, 0.85, 0.6, 0.5, 64), _tempDir);
    }

    private static readonly double[] SweepThresholds = [0.6, 0.7, 0.85, 0.95];

    [Fact]
    public async Task Replay_NoSourcesRoutesToResearch()
    {
        var goldens = new[]
        {
            ("Compare three AI-agent tools", "No sources yet"),
            ("Survey vector stores", "None yet, not started"),
        };

        foreach (var (goal, work) in goldens)
        {
            var (handoff, _) = await _router.RouteAsync(goal, work, null, CancellationToken.None);
            handoff.Destination.Should().Be("research");
        }
    }

    [Fact]
    public async Task Replay_EvidenceRoutesToWriteAtLowThreshold()
    {
        var (handoff, _) = await _router.RouteAsync(
            "Compare three AI-agent tools",
            "Sources collected. Evidence gathered. Findings summarized. Ready to draft.",
            0.6, CancellationToken.None);

        handoff.Choice.Should().Be("write");
        handoff.Destination.Should().Be("write");
    }

    [Fact]
    public async Task Replay_UnclearAndCompleteRouteToReview()
    {
        var unclear = await _router.RouteAsync("Maybe compare tools?", "Goal unclear, out of scope ideas only", null, CancellationToken.None);
        unclear.Handoff.Destination.Should().Be("review");

        var complete = await _router.RouteAsync("Compare tools", "Briefing complete and published", null, CancellationToken.None);
        complete.Handoff.Destination.Should().Be("review");
    }

    [Fact]
    public async Task Sweep_ReviewRateNeverDecreasesAsAutoRises()
    {
        var states = new[]
        {
            ("Sweep evidence alpha", "Sources collected. Evidence gathered. Findings summarized. Ready to draft."),
            ("Sweep tbd beta", "Still tbd, vague direction"),
        };

        var reviewCounts = new List<int>();
        foreach (var threshold in SweepThresholds)
        {
            var reviews = 0;
            foreach (var (goal, work) in states)
            {
                var (handoff, _) = await _router.RouteAsync($"{goal} {threshold}", work, threshold, CancellationToken.None);
                if (handoff.Destination == "review")
                    reviews++;
            }
            reviewCounts.Add(reviews);
        }

        reviewCounts.Should().HaveCount(SweepThresholds.Length);
        for (var i = 1; i < reviewCounts.Count; i++)
            reviewCounts[i].Should().BeGreaterThanOrEqualTo(reviewCounts[i - 1]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
        GC.SuppressFinalize(this);
    }
}
