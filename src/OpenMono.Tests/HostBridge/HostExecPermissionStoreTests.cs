using FluentAssertions;
using OpenMono.HostBridge;
using OpenMono.Permissions;

namespace OpenMono.Tests.HostBridge;

public class HostExecPermissionStoreTests
{
    [Fact]
    public void HostExec_RoundTrips_WithScope()
    {
        var store = new HostExecPermissionStore();
        store.RememberHostExec(allow: true, GrantScope.Session);

        store.TryResolveHostExec().Should().Be(new PermissionGrant(true, GrantScope.Session));
    }

    [Fact]
    public void Sudo_RoundTrips_IndependentlyFromHostExec()
    {
        var store = new HostExecPermissionStore();
        store.RememberSudo(allow: false, GrantScope.Session);

        store.TryResolveSudo()!.Allow.Should().BeFalse();
        store.TryResolveHostExec().Should().BeNull();
    }

    [Fact]
    public void BaseToolGrants_Coexist_WithHostKeys()
    {
        var store = new HostExecPermissionStore();
        store.Remember("Bash", true, GrantScope.Session);
        store.RememberHostExec(true, GrantScope.Session);

        store.TryResolve("Bash").Should().NotBeNull();
        store.TryResolveHostExec().Should().NotBeNull();
    }

    [Fact]
    public void Fork_PreservesSubclassType_AndGrants()
    {
        var store = new HostExecPermissionStore();
        store.RememberHostExec(true, GrantScope.Session);
        store.Remember("Bash", true, GrantScope.Session);

        var fork = store.Fork();

        fork.Should().BeOfType<HostExecPermissionStore>();
        ((HostExecPermissionStore)fork).TryResolveHostExec().Should().NotBeNull();
    }

    [Fact]
    public void Clear_WipesHostKeys_Too()
    {
        var store = new HostExecPermissionStore();
        store.RememberHostExec(true, GrantScope.Session);
        store.RememberSudo(true, GrantScope.Session);

        store.Clear();

        store.TryResolveHostExec().Should().BeNull();
        store.TryResolveSudo().Should().BeNull();
    }

    [Fact]
    public void ForgetHostExec_DropsGrant()
    {
        var store = new HostExecPermissionStore();
        store.RememberHostExec(true, GrantScope.Once);
        store.ForgetHostExec();

        store.TryResolveHostExec().Should().BeNull();
    }
}
