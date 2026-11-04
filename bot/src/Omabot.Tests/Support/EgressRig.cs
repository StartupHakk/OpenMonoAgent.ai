// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using Omabot.Common;
using Omabot.Egress;

namespace Omabot.Tests.Support;

/// <summary>Names to addresses for gate tests; counts lookups. A function can change answers between lookups (DNS rebinding).</summary>
public sealed class MapResolver(Func<string, int, string[]> answer) : IHostResolver
{
    private int _calls;
    public int Calls => _calls;

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        var n = Interlocked.Increment(ref _calls);
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal)) return Task.FromResult(new[] { literal });
        var a = answer(host, n);
        if (a.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
        return Task.FromResult(a.Select(IPAddress.Parse).ToArray());
    }
}

/// <summary>Sends the gate's outbound connection for a (public) address and port to a local test server.</summary>
public sealed class MapConnector(Dictionary<(string Ip, int Port), int> map) : IConnector
{
    public List<IPEndPoint> Seen { get; } = [];

    public async Task<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        lock (Seen) Seen.Add(endpoint);
        if (!map.TryGetValue((endpoint.Address.ToString(), endpoint.Port), out var local)) throw new SocketException((int)SocketError.ConnectionRefused);
        var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, local, ct);
        return c.GetStream();
    }
}

/// <summary>A local TCP server that echoes, or answers HTTP with a fixed reply, and records what it got.</summary>
public sealed class TestServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public List<string> Received { get; } = [];

    public static TestServer Echo() => new(null);

    public static TestServer Http(string reply) => new(reply);

    private TestServer(string? httpReply)
    {
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch (Exception) { break; }
                _ = Task.Run(async () =>
                {
                    using (c)
                    {
                        var s = c.GetStream();
                        var buf = new byte[8192];
                        try
                        {
                            if (httpReply is null)
                            {
                                int n;
                                while ((n = await s.ReadAsync(buf, _cts.Token)) > 0)
                                {
                                    lock (Received) Received.Add(Encoding.ASCII.GetString(buf, 0, n));
                                    await s.WriteAsync(buf.AsMemory(0, n), _cts.Token);
                                }
                            }
                            else
                            {
                                var sb = new StringBuilder();
                                while (!sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                                {
                                    var n = await s.ReadAsync(buf, _cts.Token);
                                    if (n == 0) break;
                                    sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                                }
                                lock (Received) Received.Add(sb.ToString());
                                await s.WriteAsync(Encoding.ASCII.GetBytes(httpReply), _cts.Token);
                            }
                        }
                        catch (Exception) { }
                    }
                });
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        _cts.Dispose();
    }
}

/// <summary>A running gate on loopback with a policy, a resolver and a connector, and a client that speaks to it.</summary>
public sealed class EgressRig : IAsyncDisposable
{
    public EgressPolicy Policy { get; set; }
    public EgressProxy Proxy { get; }
    public EgressLog Log { get; }
    public MapResolver Resolver { get; }
    public MapConnector Connector { get; }
    public Dictionary<string, string> Tokens { get; } = new(StringComparer.Ordinal);

    public EgressRig(string logPath, IClock clock, MapResolver resolver, MapConnector connector, params (string Name, string Kind, string[] Allow)[] clients)
        : this(logPath, clock, resolver, connector, 32, clients)
    {
    }

    public EgressRig(string logPath, IClock clock, MapResolver resolver, MapConnector connector, int maxPerClient, params (string Name, string Kind, string[] Allow)[] clients)
    {
        Policy = new EgressPolicy { BlockedHosts = ["x.com", "twitter.com"] };
        foreach (var (name, kind, allow) in clients)
        {
            var token = "egt_" + Guid.NewGuid().ToString("N");
            Tokens[name] = token;
            Policy.Clients.Add(new EgressClient { Name = name, Kind = kind, TokenSha256 = SecureFile.Sha256Hex(token), Allow = [.. allow] });
        }
        Resolver = resolver;
        Connector = connector;
        Log = new EgressLog(logPath, clock);
        Proxy = new EgressProxy(() => Policy, resolver, connector, Log) { MaxPerClient = maxPerClient };
        Proxy.Start(new IPEndPoint(IPAddress.Loopback, 0));
    }

    /// <summary>The status line and headers of the last response.</summary>
    public string LastHead { get; private set; } = "";

    public string Auth(string name) => "Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(name + ":" + Tokens[name])) + "\r\n";

    /// <summary>Sends a request head and returns the status code, the open stream and what came after the head.</summary>
    public async Task<(int Status, TcpClient Client, NetworkStream Stream, string After)> SendAsync(string head)
    {
        var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, Proxy.Port);
        var s = c.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        var sb = new StringBuilder();
        var buf = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var n = await s.ReadAsync(buf, cts.Token);
            if (n == 0) break;
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
        }
        var text = sb.ToString();
        var status = text.Length >= 12 && int.TryParse(text.AsSpan(9, 3), out var code) ? code : 0;
        var i = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        LastHead = i < 0 ? text : text[..i];
        return (status, c, s, i < 0 ? "" : text[(i + 4)..]);
    }

    public async Task<int> StatusAsync(string head)
    {
        var (status, c, _, _) = await SendAsync(head);
        c.Dispose();
        return status;
    }

    public async ValueTask DisposeAsync() => await Proxy.DisposeAsync();
}
