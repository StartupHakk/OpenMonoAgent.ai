using OpenMono.Permissions;

namespace OpenMono.HostBridge;

/// <summary>
/// Agent-host-mode extension of <see cref="PermissionStore"/>.
/// Adds bare-metal grants (HOST_EXEC authorization, sudo elevation) that only
/// exist for sessions running ON the host. Sandboxed TUI and ACP/VSCode paths
/// never see these keys — core tool behavior changes once, in the base class.
///
/// Host-exec/sudo grants share the base tool-grant namespace under the same
/// reserved keys the bridge has always used (<c>host-exec</c>, <c>sudo</c>).
/// No container tool uses those names, so there is no collision.
/// </summary>
public sealed class HostExecPermissionStore : PermissionStore
{
    private const string HostExecKey = "host-exec";
    private const string SudoKey = "sudo";

    public PermissionGrant? TryResolveHostExec() => GetGrant(HostExecKey);

    public void RememberHostExec(bool allow, GrantScope scope) =>
        AddGrant(HostExecKey, new PermissionGrant(allow, scope));

    public void ForgetHostExec() => RemoveGrant(HostExecKey);

    public PermissionGrant? TryResolveSudo() => GetGrant(SudoKey);

    public void RememberSudo(bool allow, GrantScope scope) =>
        AddGrant(SudoKey, new PermissionGrant(allow, scope));

    public void ForgetSudo() => RemoveGrant(SudoKey);

    protected override PermissionStore CreateEmpty() => new HostExecPermissionStore();
}
