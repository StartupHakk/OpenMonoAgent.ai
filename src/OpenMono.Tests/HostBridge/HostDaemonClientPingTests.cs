using System.Net;
using System.Text;
using FluentAssertions;
using OpenMono.HostBridge;

namespace OpenMono.Tests.HostBridge;

/// <summary>
/// The "host daemon not reachable" error used to hide three different
/// failures: nothing listening, a port that answers HTTP but is not the
/// daemon (404 prefix mismatch, port collision), and a dead daemon. Ping
/// must tell them apart so the error message is actionable.
/// </summary>
public class HostDaemonClientPingTests
{
    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Ping_NothingListening_ReportsUnreachable()
    {
        using var client = new HostDaemonClient($"http://127.0.0.1:{GetFreePort()}");
        var ping = await client.PingAsync(CancellationToken.None);
        ping.Daemon.Should().BeFalse();
        ping.Reachable.Should().BeFalse("nothing answered at the TCP level");
    }

    [Fact]
    public async Task Ping_PortServedByOtherService_ReportsReachableButNotDaemon()
    {
        // Mimics the Docker Desktop 404 case: something answers HTTP on the
        // port but never with daemon JSON.
        var port = GetFreePort();
        using var fake = new HttpListener();
        fake.Prefixes.Add($"http://127.0.0.1:{port}/");
        fake.Start();
        var serve = Task.Run(async () =>
        {
            while (fake.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await fake.GetContextAsync(); }
                catch { return; }
                var body = Encoding.UTF8.GetBytes("<h1>Not Found</h1>");
                ctx.Response.StatusCode = 404;
                ctx.Response.ContentType = "text/html";
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
        });

        using var client = new HostDaemonClient($"http://127.0.0.1:{port}");
        var ping = await client.PingAsync(CancellationToken.None);
        ping.Daemon.Should().BeFalse();
        ping.Reachable.Should().BeTrue("an HTTP response means the route is served");
        fake.Stop();
    }

    [Fact]
    public async Task Pair_PortServedByOtherService_ReportsWrongRouteNotDown()
    {
        var port = GetFreePort();
        using var fake = new HttpListener();
        fake.Prefixes.Add($"http://127.0.0.1:{port}/");
        fake.Start();
        var serve = Task.Run(async () =>
        {
            while (fake.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await fake.GetContextAsync(); }
                catch { return; }
                var body = Encoding.UTF8.GetBytes("<h1>Not Found</h1>");
                ctx.Response.StatusCode = 404;
                ctx.Response.ContentType = "text/html";
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
        });

        var gw = new OpenMono.Session.HostGateway(
            $"http://127.0.0.1:{port}", "ping-wrong-route", "/tmp", "secret");
        var error = await gw.PairAsync();
        error.Should().NotBeNull()
            .And.Contain("not the host-bridge daemon",
                "a port that answers but is not the daemon must not read as 'not reachable'");
        fake.Stop();
    }

    [Fact]
    public async Task Ping_RealDaemon_ReportsReachableAndDaemon()
    {
        var config = new BridgeConfig { PairSecret = "ping-test-secret-0f1e2d" };
        var identity = new HostIdentity("", SudoPolicy.Ask, null);
        await using var daemon = new HostDaemon(config, identity, Path.GetTempPath(), GetFreePort());
        daemon.Start();

        using var client = new HostDaemonClient($"http://127.0.0.1:{daemon.Port}");
        var ping = await client.PingAsync(CancellationToken.None);
        ping.Daemon.Should().BeTrue();
        ping.Reachable.Should().BeTrue();
    }
}
