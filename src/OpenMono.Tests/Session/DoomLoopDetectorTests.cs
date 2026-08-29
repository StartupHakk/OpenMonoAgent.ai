using FluentAssertions;
using OpenMono.Session;

namespace OpenMono.Tests.Session;

public class DoomLoopDetectorTests
{
    [Fact]
    public void IdenticalCalls_FireFromThirdBatch()
    {
        var d = new DoomLoopDetector();
        var calls = new List<ToolCall> { new() { Id = "1", Name = "T", Arguments = "{}" } };

        // Period-1 loop needs 3 identical signatures back-to-back.
        d.Check(calls).Should().BeFalse();
        d.Check(calls).Should().BeFalse();
        d.Check(calls).Should().BeTrue();
    }

    [Fact]
    public void PeriodTwoLoop_WithSameArguments_Fires()
    {
        var d = new DoomLoopDetector();
        var a = new List<ToolCall> { new() { Id = "1", Name = "T", Arguments = "{}" } };
        var b = new List<ToolCall> { new() { Id = "2", Name = "U", Arguments = "{}" } };

        // A,B,A,B is still a loop (period 2) and must be detected, not treated as progress.
        // The period-2 pattern needs 4 batches in the window.
        d.Check(a).Should().BeFalse();
        d.Check(b).Should().BeFalse();
        d.Check(a).Should().BeFalse();
        d.Check(b).Should().BeTrue();
    }

    [Fact]
    public void VariedCalls_DoNotFire()
    {
        var d = new DoomLoopDetector();

        for (var i = 0; i < 12; i++)
        {
            var withArg = new List<ToolCall> { new() { Id = $"{i}", Name = "T", Arguments = $"{{ \"i\": {i} }}" } };
            d.Check(withArg).Should().BeFalse();
        }
    }

    [Fact]
    public void NormalizedSignature_IgnoresArgumentKeyOrder()
    {
        DoomLoopDetector.SignatureFor([
            new ToolCall { Id = "1", Name = "T", Arguments = """{"b":1,"a":2}""" },
        ]).Should().Be(
            DoomLoopDetector.SignatureFor([
                new ToolCall { Id = "2", Name = "T", Arguments = """{"a":2,"b":1}""" },
            ]));
    }

    [Fact]
    public void Reset_ClearsDetectorHistory()
    {
        var d = new DoomLoopDetector();
        var calls = new List<ToolCall> { new() { Id = "1", Name = "T", Arguments = "{}" } };

        d.Check(calls);
        d.Check(calls);
        d.Reset();
        d.Check(calls).Should().BeFalse("after Reset the 3-repeat window restarts");
        d.Check(calls).Should().BeFalse();
        d.Check(calls).Should().BeTrue();
    }
}