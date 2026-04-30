using FluentAssertions;
using OpenMono.Session;
using OpenMono.Tui;

namespace OpenMono.Tests.Tui;

public class ApprovalControllerTests
{
    private static ToolCall MakeCall(string name = "TestTool") =>
        new() { Id = "t1", Name = name, Arguments = "{}" };

    [Fact]
    public void InitialState_ApprovalModeOff()
    {
        var ac = new ApprovalController();
        ac.ManualApprovalMode.Should().BeFalse();
    }

    [Fact]
    public async Task CheckApproval_WhenOff_ReturnsAllow()
    {
        var ac = new ApprovalController();
        var result = await ac.CheckApprovalAsync(MakeCall(), CancellationToken.None);
        result.Should().Be(ApprovalDecision.Allow);
    }

    [Fact]
    public async Task CheckApproval_WhenOn_NoCallback_ReturnsAllow()
    {
        var ac = new ApprovalController();
        ac.ToggleApprovalMode();

        var result = await ac.CheckApprovalAsync(MakeCall(), CancellationToken.None);
        result.Should().Be(ApprovalDecision.Allow);
    }

    [Fact]
    public async Task CheckApproval_WhenOn_DelegatesToCallback()
    {
        var ac = new ApprovalController();
        ac.ToggleApprovalMode();
        ac.RequestApprovalFunc = (_, _) => Task.FromResult(ApprovalDecision.Deny);

        var result = await ac.CheckApprovalAsync(MakeCall(), CancellationToken.None);
        result.Should().Be(ApprovalDecision.Deny);
    }

    [Fact]
    public async Task CheckApproval_AllowAll_SkipsSubsequentCalls()
    {
        var callCount = 0;
        var ac = new ApprovalController();
        ac.ToggleApprovalMode();
        ac.RequestApprovalFunc = (_, _) =>
        {
            callCount++;
            return Task.FromResult(ApprovalDecision.AllowAll);
        };

        var r1 = await ac.CheckApprovalAsync(MakeCall(), CancellationToken.None);
        r1.Should().Be(ApprovalDecision.Allow);

        var r2 = await ac.CheckApprovalAsync(MakeCall(), CancellationToken.None);
        r2.Should().Be(ApprovalDecision.Allow);

        callCount.Should().Be(1, "second call should skip the dialog");
    }

    [Fact]
    public async Task ResetTurn_ResetsAllowAllOverride()
    {
        var callCount = 0;
        var ac = new ApprovalController();
        ac.ToggleApprovalMode();
        ac.RequestApprovalFunc = (_, _) =>
        {
            callCount++;
            return Task.FromResult(ApprovalDecision.AllowAll);
        };

        await ac.CheckApprovalAsync(MakeCall(), CancellationToken.None);
        callCount.Should().Be(1);

        ac.ResetTurn();
        await ac.CheckApprovalAsync(MakeCall(), CancellationToken.None);
        callCount.Should().Be(2, "after ResetTurn, dialog should show again");
    }

    [Fact]
    public async Task CheckApproval_DenyAll_ReturnsDenyAll()
    {
        var ac = new ApprovalController();
        ac.ToggleApprovalMode();
        ac.RequestApprovalFunc = (_, _) => Task.FromResult(ApprovalDecision.DenyAll);

        var result = await ac.CheckApprovalAsync(MakeCall(), CancellationToken.None);
        result.Should().Be(ApprovalDecision.DenyAll);
    }

    [Fact]
    public void ToggleApprovalMode_FiresEvent()
    {
        var ac = new ApprovalController();
        var states = new List<bool>();
        ac.OnApprovalModeChanged += (_, on) => states.Add(on);

        ac.ToggleApprovalMode();
        ac.ToggleApprovalMode();

        states.Should().Equal([true, false]);
    }

    [Fact]
    public async Task CallbackReceivesCorrectToolCall()
    {
        var ac = new ApprovalController();
        ac.ToggleApprovalMode();

        ToolCall? received = null;
        ac.RequestApprovalFunc = (call, _) =>
        {
            received = call;
            return Task.FromResult(ApprovalDecision.Allow);
        };

        var tc = new ToolCall { Id = "x1", Name = "FileRead", Arguments = "{\"path\":\"/foo\"}" };
        await ac.CheckApprovalAsync(tc, CancellationToken.None);

        received.Should().NotBeNull();
        received!.Name.Should().Be("FileRead");
        received.Id.Should().Be("x1");
    }
}
