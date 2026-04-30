using OpenMono.Session;

namespace OpenMono.Tui;

public enum ApprovalDecision
{
    Allow,
    Deny,
    AllowAll,
    DenyAll
}

public sealed class ApprovalController
{
    private volatile bool _manualApprovalMode;
    private bool _allowAllForTurn;

    public bool ManualApprovalMode => _manualApprovalMode;

    public event EventHandler<bool>? OnApprovalModeChanged;

    public Func<ToolCall, CancellationToken, Task<ApprovalDecision>>? RequestApprovalFunc { get; set; }

    public void ToggleApprovalMode()
    {
        _manualApprovalMode = !_manualApprovalMode;
        _allowAllForTurn = false;
        OnApprovalModeChanged?.Invoke(this, _manualApprovalMode);
    }

    public void ResetTurn()
    {
        _allowAllForTurn = false;
    }

    public async Task<ApprovalDecision> CheckApprovalAsync(ToolCall call, CancellationToken ct)
    {
        if (!_manualApprovalMode || _allowAllForTurn)
            return ApprovalDecision.Allow;

        if (RequestApprovalFunc is null)
            return ApprovalDecision.Allow;

        var decision = await RequestApprovalFunc(call, ct);

        switch (decision)
        {
            case ApprovalDecision.AllowAll:
                _allowAllForTurn = true;
                return ApprovalDecision.Allow;
            case ApprovalDecision.DenyAll:

                return ApprovalDecision.DenyAll;
            default:
                return decision;
        }
    }
}
