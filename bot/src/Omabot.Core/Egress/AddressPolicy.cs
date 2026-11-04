// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Sockets;

namespace Omabot.Egress;

/// <summary>
/// The egress address policy (plan 7.7). Only public addresses are allowed.
/// Refusal is by address after resolving, so a name, a redirect or DNS
/// rebinding cannot get around it. Used by the egress gate and by routine
/// checks that reach the network.
/// </summary>
public static class AddressPolicy
{
    private sealed record Range(IPAddress Network, int Prefix, string Reason);

    private static readonly Range[] V4 =
    [
        new(IPAddress.Parse("0.0.0.0"), 8, "this network (0.0.0.0/8)"),
        new(IPAddress.Parse("10.0.0.0"), 8, "private (10.0.0.0/8)"),
        new(IPAddress.Parse("100.64.0.0"), 10, "carrier grade NAT (100.64.0.0/10)"),
        new(IPAddress.Parse("127.0.0.0"), 8, "loopback (127.0.0.0/8)"),
        new(IPAddress.Parse("169.254.0.0"), 16, "link local and cloud metadata (169.254.0.0/16)"),
        new(IPAddress.Parse("172.16.0.0"), 12, "private (172.16.0.0/12)"),
        new(IPAddress.Parse("192.0.0.0"), 24, "protocol assignments (192.0.0.0/24)"),
        new(IPAddress.Parse("192.0.2.0"), 24, "documentation (192.0.2.0/24)"),
        new(IPAddress.Parse("192.88.99.0"), 24, "6to4 relay (192.88.99.0/24)"),
        new(IPAddress.Parse("192.168.0.0"), 16, "private (192.168.0.0/16)"),
        new(IPAddress.Parse("198.18.0.0"), 15, "benchmarking (198.18.0.0/15)"),
        new(IPAddress.Parse("198.51.100.0"), 24, "documentation (198.51.100.0/24)"),
        new(IPAddress.Parse("203.0.113.0"), 24, "documentation (203.0.113.0/24)"),
        new(IPAddress.Parse("224.0.0.0"), 4, "multicast (224.0.0.0/4)"),
        new(IPAddress.Parse("240.0.0.0"), 4, "reserved and broadcast (240.0.0.0/4)"),
    ];

    private static readonly Range[] V6 =
    [
        new(IPAddress.Parse("::"), 128, "unspecified (::)"),
        new(IPAddress.Parse("::1"), 128, "loopback (::1)"),
        new(IPAddress.Parse("100::"), 64, "discard (100::/64)"),
        new(IPAddress.Parse("2001::"), 23, "protocol assignments and Teredo (2001::/23)"),
        new(IPAddress.Parse("2001:db8::"), 32, "documentation (2001:db8::/32)"),
        new(IPAddress.Parse("fc00::"), 7, "unique local (fc00::/7)"),
        new(IPAddress.Parse("fe80::"), 10, "link local (fe80::/10)"),
        new(IPAddress.Parse("fec0::"), 10, "site local (fec0::/10)"),
        new(IPAddress.Parse("ff00::"), 8, "multicast (ff00::/8)"),
    ];

    /// <summary>Why an address is refused, or null when it is a public address.</summary>
    public static string? Refusal(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv4MappedToIPv6) return Refusal(ip.MapToIPv4()) is { } r ? r + " as IPv4-mapped IPv6" : null;
            var b = ip.GetAddressBytes();
            // IPv4-compatible (::a.b.c.d), NAT64 (64:ff9b::/96) and 6to4 (2002::/16) carry an IPv4 address.
            if (b.Take(12).All(x => x == 0) && !(b[12] == 0 && b[13] == 0 && b[14] == 0 && b[15] <= 1))
                return Refusal(new IPAddress(b[12..16])) is { } r1 ? r1 + " as IPv4-compatible IPv6" : null;
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.Skip(4).Take(8).All(x => x == 0))
                return Refusal(new IPAddress(b[12..16])) is { } r2 ? r2 + " through NAT64" : null;
            if (b[0] == 0x20 && b[1] == 0x02)
                return Refusal(new IPAddress(b[2..6])) is { } r3 ? r3 + " through 6to4" : null;
            foreach (var range in V6) if (InRange(b, range)) return range.Reason;
            return null;
        }
        if (ip.AddressFamily != AddressFamily.InterNetwork) return "not an IP address";
        var a = ip.GetAddressBytes();
        foreach (var range in V4) if (InRange(a, range)) return range.Reason;
        return null;
    }

    public static bool IsPublic(IPAddress ip) => Refusal(ip) is null;

    private static bool InRange(byte[] addr, Range range)
    {
        var net = range.Network.GetAddressBytes();
        if (net.Length != addr.Length) return false;
        var bits = range.Prefix;
        for (var i = 0; i < addr.Length && bits > 0; i++, bits -= 8)
        {
            var mask = bits >= 8 ? (byte)0xff : (byte)(0xff << (8 - bits));
            if ((addr[i] & mask) != (net[i] & mask)) return false;
        }
        return true;
    }

    /// <summary>
    /// True when the host is on the allowlist: an exact name, or a "*.example.com"
    /// entry for any name under example.com. Names are compared literally, lower
    /// case, without a trailing dot. IP literals are never on an allowlist.
    /// </summary>
    public static bool HostAllowed(string host, IEnumerable<string> allow)
    {
        var h = Normalize(host);
        if (h.Length == 0 || IPAddress.TryParse(h.Trim('[', ']'), out _)) return false;
        foreach (var raw in allow)
        {
            var a = Normalize(raw);
            if (a.Length == 0) continue;
            if (a.StartsWith("*.", StringComparison.Ordinal))
            {
                if (h.EndsWith(a[1..], StringComparison.Ordinal) && h.Length > a.Length - 1) return true;
            }
            else if (h == a) return true;
        }
        return false;
    }

    /// <summary>True when the host is a blocked host or a name under one (x.com blocks api.x.com).</summary>
    public static bool HostBlocked(string host, IEnumerable<string> blocked)
    {
        var h = Normalize(host);
        return blocked.Select(Normalize).Where(b => b.Length > 0).Any(b => h == b || h.EndsWith("." + b, StringComparison.Ordinal));
    }

    public static string Normalize(string host) => host.Trim().TrimEnd('.').ToLowerInvariant();
}

/// <summary>Name resolution, swappable in tests (DNS rebinding cases).</summary>
public interface IHostResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed class DnsResolver : IHostResolver
{
    public static readonly DnsResolver Instance = new();

    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal)) return [literal];
        return await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Resolves a host once and picks the address to connect to. Every resolved
/// address must be public; the caller then connects to exactly the address
/// that was checked, never to a fresh lookup.
/// </summary>
public static class SafeResolve
{
    public sealed record Result(IPAddress? Address, string? Refusal);

    public static async Task<Result> PickAsync(IHostResolver resolver, string host, CancellationToken ct)
    {
        IPAddress[] addrs;
        try
        {
            addrs = await resolver.ResolveAsync(host, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return new Result(null, "name does not resolve");
        }
        if (addrs.Length == 0) return new Result(null, "name does not resolve");
        foreach (var a in addrs)
        {
            if (AddressPolicy.Refusal(a) is { } why) return new Result(null, $"resolves to a refused address: {why}");
        }
        return new Result(addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs[0], null);
    }
}
