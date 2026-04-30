using FluentAssertions;
using OpenMono.Tui;

namespace OpenMono.Tests.Tui;

public class PauseControllerTests
{
    [Fact]
    public void InitialState_NotPaused()
    {
        var pc = new PauseController();
        pc.IsPaused.Should().BeFalse();
    }

    [Fact]
    public void TogglePause_Pauses()
    {
        var pc = new PauseController();
        pc.TogglePause();
        pc.IsPaused.Should().BeTrue();
    }

    [Fact]
    public void TogglePause_Twice_Resumes()
    {
        var pc = new PauseController();
        pc.TogglePause();
        pc.TogglePause();
        pc.IsPaused.Should().BeFalse();
    }

    [Fact]
    public async Task WaitIfPaused_ReturnsImmediately_WhenNotPaused()
    {
        var pc = new PauseController();
        var task = pc.WaitIfPausedAsync(CancellationToken.None);
        task.IsCompleted.Should().BeTrue();
        await task;
    }

    [Fact]
    public async Task WaitIfPaused_Blocks_WhenPaused()
    {
        var pc = new PauseController();
        pc.TogglePause();

        var waitTask = pc.WaitIfPausedAsync(CancellationToken.None);
        await Task.Delay(50);
        waitTask.IsCompleted.Should().BeFalse("should be waiting while paused");

        pc.TogglePause();
        await Task.Delay(50);
        waitTask.IsCompleted.Should().BeTrue("should complete after resume");
    }

    [Fact]
    public async Task WaitIfPaused_RespectsCancellation()
    {
        var pc = new PauseController();
        pc.TogglePause();

        using var cts = new CancellationTokenSource(100);
        var act = () => pc.WaitIfPausedAsync(cts.Token);
        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public void OnPauseStateChanged_FiresOnPause()
    {
        var pc = new PauseController();
        var states = new List<bool>();
        pc.OnPauseStateChanged += (_, paused) => states.Add(paused);

        pc.TogglePause();
        pc.TogglePause();

        states.Should().Equal([true, false]);
    }

    [Fact]
    public async Task MultipleConcurrentWaiters_AllResumed()
    {
        var pc = new PauseController();
        pc.TogglePause();

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => pc.WaitIfPausedAsync(CancellationToken.None))
            .ToList();

        tasks.Should().AllSatisfy(t => t.IsCompleted.Should().BeFalse());

        pc.TogglePause();
        await Task.WhenAll(tasks);
    }
}
