using FluentAssertions;
using OpenMono.Session;

namespace OpenMono.Tests.HostBridge;

/// <summary>
/// The default daemon URL must stay the literal host.docker.internal on every
/// runtime: Docker Desktop (vpnkit), Colima/Lima (vmnet) and Linux docker
/// (host-gateway) all route it, and HostDaemonClient's Host-header override
/// matches the loopback prefix the daemon always binds. An earlier rewrite to
/// the resolved IPv4 — gated on OPENMONO_HOST_OS, falling back to the
/// container's OS when unset — dialed the vpnkit gateway with no header
/// override and 404'd as "daemon not reachable". These guard against
/// reintroducing it.
/// </summary>
public class HostGatewayBootstrapTests : IDisposable
{
    public void Dispose() => Reset();

    private static void Reset()
    {
        Environment.SetEnvironmentVariable(HostGatewayBootstrap.UrlEnv, null);
        Environment.SetEnvironmentVariable(HostGatewayBootstrap.HostOsEnv, null);
    }

    [Fact]
    public void ResolveBaseUrl_MacHost_KeepsLiteralDockerGatewayName()
    {
        Environment.SetEnvironmentVariable(HostGatewayBootstrap.UrlEnv, "http://host.docker.internal:7476");
        Environment.SetEnvironmentVariable(HostGatewayBootstrap.HostOsEnv, "Darwin");
        HostGatewayBootstrap.ResolveBaseUrl().Should().Be("http://host.docker.internal:7476");
    }

    [Fact]
    public void ResolveBaseUrl_ExplicitPortPreserved_OnMacHost()
    {
        Environment.SetEnvironmentVariable(HostGatewayBootstrap.UrlEnv, "http://host.docker.internal:7999");
        Environment.SetEnvironmentVariable(HostGatewayBootstrap.HostOsEnv, "Darwin");
        HostGatewayBootstrap.ResolveBaseUrl().Should().Be("http://host.docker.internal:7999");
    }

    [Fact]
    public void ResolveBaseUrl_NonGatewayUrl_UsedVerbatim()
    {
        Environment.SetEnvironmentVariable(HostGatewayBootstrap.UrlEnv, "http://192.168.5.2:7476");
        Environment.SetEnvironmentVariable(HostGatewayBootstrap.HostOsEnv, "Darwin");
        HostGatewayBootstrap.ResolveBaseUrl().Should().Be("http://192.168.5.2:7476");
    }

    [Fact]
    public void ResolveBaseUrl_EmptyHostOs_StillKeepsLiteralDockerGatewayName()
    {
        // The live regression: launcher passed OPENMONO_HOST_OS="" (empty,
        // not unset) on a macOS host — the old OS best-guess fired, resolved
        // the name to the vpnkit gateway IP and pairing 404'd.
        Environment.SetEnvironmentVariable(HostGatewayBootstrap.UrlEnv, "");
        Environment.SetEnvironmentVariable(HostGatewayBootstrap.HostOsEnv, "");
        HostGatewayBootstrap.ResolveBaseUrl().Should().Be("http://host.docker.internal:7476");
    }
}
