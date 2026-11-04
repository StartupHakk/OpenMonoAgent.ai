// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Omabot.Config;

namespace Omabot.Egress;

/// <summary>One Docker network the roster owns: fixed subnet and Linux bridge name, so firewall rules can key on it.</summary>
public sealed record NetworkSpec(string Name, bool Internal, string Subnet, string Gateway, string Bridge);

/// <summary>
/// The three roster networks (plan 7.7). Bots and desktops sit on internal
/// networks with no default route: bots reach only the gateway (and
/// agent-host when allowed) on the host and the egress gate; desktops reach
/// only the gate. The egress network is the one way out and only the gate
/// joins it.
/// </summary>
public static partial class Networks
{
    public static NetworkSpec Bots(RosterConfig c) => new(c.Bots.Network, true, c.Bots.Subnet, GatewayOf(c.Bots.Subnet), c.Bots.Bridge);

    public static NetworkSpec Desktops(RosterConfig c) => new(c.Desktops.Network, true, c.Desktops.Subnet, GatewayOf(c.Desktops.Subnet), c.Desktops.Bridge);

    public static NetworkSpec Egress(RosterConfig c) => new(c.Egress.Network, false, c.Egress.Subnet, GatewayOf(c.Egress.Subnet), c.Egress.Bridge);

    public static IReadOnlyList<NetworkSpec> All(RosterConfig c) => [Bots(c), Desktops(c), Egress(c)];

    public static NetworkSpec? Find(RosterConfig c, string name) => All(c).FirstOrDefault(n => n.Name == name);

    [GeneratedRegex("^br-[a-z0-9]{1,12}$")]
    private static partial Regex BridgeName();

    [GeneratedRegex("^[a-z][a-z0-9_.-]{0,62}$")]
    private static partial Regex NetworkName();

    /// <summary>A roster subnet: IPv4, /16 to /28, no host bits.</summary>
    public static (IPAddress Network, int Prefix) Parse(string cidr) => Parse(cidr, 16, 28);

    private static (IPAddress Network, int Prefix) Parse(string cidr, int minPrefix, int maxPrefix)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) || prefix < minPrefix || prefix > maxPrefix)
            throw new InvalidDataException($"\"{cidr}\" is not an IPv4 subnet from /{minPrefix} to /{maxPrefix}.");
        var b = ip.GetAddressBytes();
        var value = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        if ((value & mask) != value) throw new InvalidDataException($"\"{cidr}\" has host bits set; use the network address.");
        return (ip, prefix);
    }

    public static string GatewayOf(string cidr)
    {
        try
        {
            var b = Parse(cidr).Network.GetAddressBytes();
            b[3] += 1;
            return new IPAddress(b).ToString();
        }
        catch (InvalidDataException)
        {
            return "";
        }
    }

    public static bool Contains(string cidr, IPAddress ip)
    {
        var (net, prefix) = Parse(cidr, 0, 32);
        var a = net.GetAddressBytes();
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return false;
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return ((uint)(a[0] << 24 | a[1] << 16 | a[2] << 8 | a[3]) & mask) == ((uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]) & mask);
    }

    private static readonly string[] PrivateRanges = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"];

    /// <summary>Throws with a plain message when the roster networks are not safe to create.</summary>
    public static void Check(RosterConfig c)
    {
        var all = All(c);
        foreach (var n in all)
        {
            if (!NetworkName().IsMatch(n.Name)) throw new InvalidDataException($"Network name \"{n.Name}\" must be lower case letters, digits, dot, dash or underscore.");
            if (!BridgeName().IsMatch(n.Bridge)) throw new InvalidDataException($"Bridge \"{n.Bridge}\" must look like br-omabot0 (br- and up to 12 letters or digits).");
            var (net, prefix) = Parse(n.Subnet);
            if (!PrivateRanges.Any(r => Contains(r, net) && prefix >= Parse(r, 0, 32).Prefix))
                throw new InvalidDataException($"Subnet {n.Subnet} for {n.Name} must be inside 10.0.0.0/8, 172.16.0.0/12 or 192.168.0.0/16.");
        }
        if (all.Select(n => n.Name).Distinct().Count() != all.Count || all.Select(n => n.Bridge).Distinct().Count() != all.Count)
            throw new InvalidDataException("The bots, desktop and egress networks need different names and bridges.");
        for (var i = 0; i < all.Count; i++)
            for (var j = i + 1; j < all.Count; j++)
                if (Contains(all[i].Subnet, Parse(all[j].Subnet).Network) || Contains(all[j].Subnet, Parse(all[i].Subnet).Network))
                    throw new InvalidDataException($"Subnets {all[i].Subnet} and {all[j].Subnet} overlap.");
        var gw = Bots(c).Gateway;
        if (c.Gateway.BindAddress != gw)
            throw new InvalidDataException($"gateway.bind_address must be {gw}, the roster network gateway for bots.subnet {c.Bots.Subnet}; bots have no route to any other address.");
    }
}
