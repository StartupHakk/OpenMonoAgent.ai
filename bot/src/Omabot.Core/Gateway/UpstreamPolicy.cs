// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;

namespace Omabot.Gateway;

/// <summary>
/// Upstream address rules (decision D20, builder choice B1). Inference is
/// always remote through the FRPS and FRPC relay; a plain http upstream that
/// is not loopback is refused unless gateway.allow_plain_http_upstream is set.
/// </summary>
public static class UpstreamPolicy
{
    public static string? Check(string? endpoint, bool allowPlainHttp)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return "No upstream is set. Set gateway.upstream (or gateway.backends) in roster.json to the relay address.";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var u)) return "The upstream is not a valid URL.";
        if (u.Scheme is not ("http" or "https")) return "The upstream must use http or https.";
        if (!string.IsNullOrEmpty(u.UserInfo)) return "Do not put credentials in the upstream URL. Use the key file.";
        if (u.Scheme == "http" && !IsLoopback(u.Host) && !allowPlainHttp)
            return "Plain http to a non-loopback upstream is refused. Use https through the relay, or set gateway.allow_plain_http_upstream to true (doctor will warn).";
        return null;
    }

    public static bool IsLoopback(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
           || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}
