using FluentAssertions;
using OpenMono.Session;

namespace OpenMono.Tests.Session;

public class DoomLoopDetectorScratch
{
    [Fact]
    public void IdenticalCalls_FireFromSecondBatch()
    {
        var d = new DoomLoopDetector();
        var calls = new List<ToolCall> { new() { Id = "1", Name = "T", Arguments = "{}" } };
        var results = new List<bool>();
        for (var i = 0; i < 6; i++) results.Add(d.Check(calls));
        // period 1 needs 3 identical signatures → first fires on the 3rd batch.
        results.Should().Equal(false, false, true, true, true, true);
    }
}
