// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Omabot.Common;

namespace Omabot.Egress;

/// <summary>
/// The egress gate (plan 7.7): an HTTP proxy for roster containers. Every
/// request names its client with Basic proxy credentials (a per-client
/// token). CONNECT host:443 makes a tunnel; an absolute http:// URL is
/// forwarded once with Connection: close. Before connecting the gate checks
/// the port, the host syntax, blocked hosts and the client's allowlist, then
/// resolves the name itself, refuses unless every address is public, and
/// connects to exactly the address it checked. A redirect is a new request
/// and is checked again. Every decision is one log line.
/// </summary>
public sealed class EgressProxy(Func<EgressPolicy> policy, IHostResolver resolver, IConnector connector, EgressLog log) : IAsyncDisposable
{
    public const int MaxHead = 16 * 1024;
    public static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MaxTunnel = TimeSpan.FromHours(1);
    public int MaxPerClient { get; init; } = 32;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _accept;
    private readonly ConcurrentDictionary<string, int> _open = new(StringComparer.Ordinal);

    public int Port => ((IPEndPoint)_listener!.LocalEndpoint).Port;

    public void Start(IPEndPoint listen)
    {
        _listener = new TcpListener(listen);
        _listener.Start(256);
        _cts = new CancellationTokenSource();
        _accept = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { break; }
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try { await HandleAsync(client.GetStream(), ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>Serves one client connection. Public so tests can drive it over any stream.</summary>
    public async Task HandleAsync(Stream stream, CancellationToken ct)
    {
        var (head, extra) = await ReadHeadAsync(stream, ct).ConfigureAwait(false);
        if (head is null)
        {
            await ReplyAsync(stream, 400, "Bad Request", "roster egress gate: the request head is missing, too large or too slow", ct).ConfigureAwait(false);
            return;
        }
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3 || first[2] is not ("HTTP/1.1" or "HTTP/1.0"))
        {
            await ReplyAsync(stream, 400, "Bad Request", "roster egress gate: not an HTTP/1.x request", ct).ConfigureAwait(false);
            return;
        }
        var (method, target) = (first[0], first[1]);
        var headers = lines.Skip(1).Where(l => l.Length > 0).Select(l => (Name: l.Split(':', 2)[0].Trim(), Value: l.Contains(':') ? l.Split(':', 2)[1].Trim() : "")).ToList();
        var p = policy();
        var client = Authenticate(p, headers);
        if (client is null)
        {
            log.Write("unknown", "unknown", HostOf(method, target), 0, false, "no valid proxy credentials");
            await ReplyAsync(stream, 407, "Proxy Authentication Required", "roster egress gate: proxy credentials are required", ct, "Proxy-Authenticate: Basic realm=\"oma-roster\"\r\n").ConfigureAwait(false);
            return;
        }

        string host;
        int port;
        Uri? url = null;
        if (method == "CONNECT")
        {
            if (!TrySplitHostPort(target, out host, out port))
            {
                log.Write(client.Name, client.Kind, target, 0, false, "bad CONNECT target");
                await ReplyAsync(stream, 400, "Bad Request", "roster egress gate: CONNECT needs host:port", ct).ConfigureAwait(false);
                return;
            }
        }
        else if (Uri.TryCreate(target, UriKind.Absolute, out url) && url.Scheme == "http" && url.UserInfo.Length == 0)
        {
            host = url.IdnHost;
            port = url.Port;
        }
        else
        {
            log.Write(client.Name, client.Kind, HostOf(method, target), 0, false, "only CONNECT and absolute http:// requests are served");
            await ReplyAsync(stream, 400, "Bad Request", "roster egress gate: use CONNECT for https, or an absolute http:// URL", ct).ConfigureAwait(false);
            return;
        }

        host = AddressPolicy.Normalize(host);
        if (p.Refusal(client, host, port) is { } refusal)
        {
            log.Write(client.Name, client.Kind, host, port, false, refusal);
            await ReplyAsync(stream, 403, "Forbidden", $"roster egress gate: refused {host}:{port} ({refusal})", ct).ConfigureAwait(false);
            return;
        }
        var pick = await SafeResolve.PickAsync(resolver, host, ct).ConfigureAwait(false);
        if (pick.Address is null)
        {
            log.Write(client.Name, client.Kind, host, port, false, pick.Refusal ?? "refused");
            await ReplyAsync(stream, 403, "Forbidden", $"roster egress gate: refused {host}:{port} ({pick.Refusal})", ct).ConfigureAwait(false);
            return;
        }
        if (!Enter(client.Name))
        {
            log.Write(client.Name, client.Kind, host, port, false, $"more than {MaxPerClient} open connections");
            await ReplyAsync(stream, 429, "Too Many Requests", "roster egress gate: too many open connections", ct).ConfigureAwait(false);
            return;
        }
        try
        {
            Stream upstream;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(ConnectTimeout);
                upstream = await connector.ConnectAsync(new IPEndPoint(pick.Address, port), cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
            {
                log.Write(client.Name, client.Kind, host, port, false, "could not connect", pick.Address);
                await ReplyAsync(stream, 502, "Bad Gateway", $"roster egress gate: could not connect to {host}:{port}", ct).ConfigureAwait(false);
                return;
            }
            await using (upstream)
            {
                log.Write(client.Name, client.Kind, host, port, true, "allowed", pick.Address);
                if (method == "CONNECT")
                {
                    await stream.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
                    if (extra.Length > 0) await upstream.WriteAsync(extra, ct).ConfigureAwait(false);
                }
                else
                {
                    var sb = new StringBuilder();
                    sb.Append(method).Append(' ').Append(url!.PathAndQuery).Append(" HTTP/1.1\r\n");
                    sb.Append("Host: ").Append(url.IsDefaultPort ? host : host + ":" + port).Append("\r\n");
                    foreach (var (name, value) in headers)
                    {
                        if (name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                            || name.Equals("Connection", StringComparison.OrdinalIgnoreCase) || name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)) continue;
                        sb.Append(name).Append(": ").Append(value).Append("\r\n");
                    }
                    sb.Append("Connection: close\r\n\r\n");
                    await upstream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
                    if (extra.Length > 0) await upstream.WriteAsync(extra, ct).ConfigureAwait(false);
                }
                await SpliceAsync(stream, upstream, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            Leave(client.Name);
        }
    }

    private static EgressClient? Authenticate(EgressPolicy p, List<(string Name, string Value)> headers)
    {
        var auth = headers.FirstOrDefault(h => h.Name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)).Value;
        if (auth is null || !auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;
        string decoded;
        try { decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..].Trim())); }
        catch (FormatException) { return null; }
        var i = decoded.IndexOf(':');
        if (i <= 0) return null;
        return p.Find(Uri.UnescapeDataString(decoded[..i]), Uri.UnescapeDataString(decoded[(i + 1)..]));
    }

    private bool Enter(string client)
    {
        var n = _open.AddOrUpdate(client, 1, (_, v) => v + 1);
        if (n <= MaxPerClient) return true;
        Leave(client);
        return false;
    }

    private void Leave(string client) => _open.AddOrUpdate(client, 0, (_, v) => Math.Max(0, v - 1));

    private static string HostOf(string method, string target)
    {
        if (method == "CONNECT") return TrySplitHostPort(target, out var h, out _) ? h : "";
        return Uri.TryCreate(target, UriKind.Absolute, out var u) ? u.Host : "";
    }

    public static bool TrySplitHostPort(string target, out string host, out int port)
    {
        host = "";
        port = 0;
        var i = target.LastIndexOf(':');
        if (i <= 0 || !int.TryParse(target[(i + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535) return false;
        host = target[..i];
        return host.Length > 0;
    }

    private static async Task<(string? Head, byte[] Extra)> ReadHeadAsync(Stream s, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(HeadTimeout);
        var buf = new byte[MaxHead + 1];
        var n = 0;
        try
        {
            while (n < buf.Length)
            {
                var read = await s.ReadAsync(buf.AsMemory(n, buf.Length - n), cts.Token).ConfigureAwait(false);
                if (read == 0) return (null, []);
                n += read;
                var end = IndexOf(buf, n, "\r\n\r\n"u8);
                if (end >= 0)
                {
                    var head = Encoding.ASCII.GetString(buf, 0, end);
                    return head.Any(ch => ch is < ' ' and not ('\r' or '\n' or '\t') || ch > '~') ? (null, []) : (head, buf[(end + 4)..n]);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, []);
        }
        return (null, []);
    }

    private static int IndexOf(byte[] buf, int len, ReadOnlySpan<byte> pattern)
        => buf.AsSpan(0, len).IndexOf(pattern);

    private static async Task SpliceAsync(Stream a, Stream b, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(MaxTunnel);
        var up = a.CopyToAsync(b, cts.Token);
        var down = b.CopyToAsync(a, cts.Token);
        try
        {
            await Task.WhenAny(up, down).ConfigureAwait(false);
            await cts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(up, down).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    private static async Task ReplyAsync(Stream s, int code, string reason, string text, CancellationToken ct, string extraHeaders = "")
    {
        var body = Encoding.UTF8.GetBytes(text + "\n");
        var head = $"HTTP/1.1 {code} {reason}\r\n{extraHeaders}Content-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        try
        {
            await s.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
            await s.WriteAsync(body, ct).ConfigureAwait(false);
            await s.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        if (_accept is not null)
        {
            try { await _accept.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
    }
}
