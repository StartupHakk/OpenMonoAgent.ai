using FluentAssertions;
using OpenMono.Permissions;

namespace OpenMono.Tests.Permissions;

public class PermissionStoreTests
{
    [Fact]
    public void TryResolve_UnknownTool_ReturnsNull()
    {
        new PermissionStore().TryResolve("Bash").Should().BeNull();
    }

    [Fact]
    public void Remember_ThenResolve_RoundTripsScope()
    {
        var store = new PermissionStore();
        store.Remember("Bash", allow: true, GrantScope.Session);

        store.TryResolve("Bash").Should().Be(new PermissionGrant(true, GrantScope.Session));
    }

    [Theory]
    [InlineData("Bash", "bash")]
    [InlineData("bash", "BASH")]
    [InlineData("Playbook:Deploy", "playbook:deploy")]
    public void Keys_AreCaseInsensitive_SingleEntry(string writeAs, string readAs)
    {
        var store = new PermissionStore();
        store.Remember(writeAs, allow: true, GrantScope.Session);

        store.TryResolve(readAs).Should().NotBeNull();
        store.Snapshot().Should().HaveCount(1);
    }

    [Fact]
    public void Deny_Grant_ResolvesAsDeny()
    {
        var store = new PermissionStore();
        store.Remember("Bash", allow: false, GrantScope.Session);

        store.TryResolve("Bash")!.Allow.Should().BeFalse();
    }

    [Fact]
    public void Forget_RemovesOnceGrant_NextCallPromptsAgain()
    {
        var store = new PermissionStore();
        store.Remember("Bash", allow: true, GrantScope.Once);
        store.Forget("Bash");

        store.TryResolve("Bash").Should().BeNull();
    }

    [Fact]
    public void Capability_RoundTrips_IndependentlyFromTools()
    {
        var store = new PermissionStore();
        store.RememberCapability("FileWriteCap", allow: true, GrantScope.Session);

        store.TryResolveCapability("filewritecap").Should().Be(new PermissionGrant(true, GrantScope.Session));
        store.TryResolve("FileWriteCap").Should().BeNull();
    }

    [Fact]
    public void Clear_EmptiesGrants()
    {
        var store = new PermissionStore();
        store.Remember("Bash", true, GrantScope.Session);
        store.RememberCapability("FileWriteCap", true, GrantScope.Session);

        store.Clear();

        store.TryResolve("Bash").Should().BeNull();
        store.TryResolveCapability("FileWriteCap").Should().BeNull();
        store.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Fork_ChildWrites_DoNotAffectParent()
    {
        var parent = new PermissionStore();
        parent.Remember("Bash", true, GrantScope.Session);

        var child = parent.Fork();
        child.Remember("Bash", false, GrantScope.Session);
        child.Remember("FileWrite", true, GrantScope.Session);

        parent.TryResolve("Bash")!.Allow.Should().BeTrue();
        parent.TryResolve("FileWrite").Should().BeNull();
    }

    [Fact]
    public void Fork_SeesParentGrants_AtForkTimeOnly()
    {
        var parent = new PermissionStore();
        parent.Remember("Bash", true, GrantScope.Session);
        var child = parent.Fork();

        parent.Remember("FileWrite", true, GrantScope.Session);

        child.TryResolve("Bash").Should().NotBeNull();
        child.TryResolve("FileWrite").Should().BeNull();
    }

    [Fact]
    public void TryResolve_BlankName_ReturnsNull()
    {
        var store = new PermissionStore();
        store.TryResolve("").Should().BeNull();
        store.TryResolve("   ").Should().BeNull();
    }
}
