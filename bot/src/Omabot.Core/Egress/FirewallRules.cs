// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using Omabot.Config;

namespace Omabot.Egress;

/// <summary>
/// The second, fail-closed layer under the egress gate (plan 7.7, adopted
/// from the restrict-computer-egress idea): iptables rules keyed on the named
/// roster bridges. Nothing is forwarded out of the bot or desktop bridges;
/// the egress bridge (the gate's way out) cannot reach the host, LAN or
/// metadata ranges; and on the host, the bot bridge reaches only the gateway
/// port (and agent-host when allowed) while desktops and the gate reach
/// nothing. The supervisor checks these rules before every container start.
/// </summary>
public static class FirewallRules
{
    public const string UserChain = "OMABOT-USER";
    public const string InputChain = "OMABOT-INPUT";

    /// <summary>Each web-multi user roster has its own chains, so applying one user's script never touches another's rules.</summary>
    public static string UserChainFor(RosterConfig c) => c.Bots.Tenant is null ? UserChain : $"OMABOT-{c.Bots.Tenant.ToUpperInvariant()}-USER";

    public static string InputChainFor(RosterConfig c) => c.Bots.Tenant is null ? InputChain : $"OMABOT-{c.Bots.Tenant.ToUpperInvariant()}-IN";

    /// <summary>Ranges the egress bridge may never reach directly (the gate refuses them by address as well).</summary>
    public static readonly string[] Refused =
    [
        "0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.168.0.0/16", "224.0.0.0/4", "240.0.0.0/4",
    ];

    /// <summary>The rules in iptables-save form, in order.</summary>
    public static IReadOnlyList<string> Rules(RosterConfig c)
    {
        var bots = Networks.Bots(c);
        var desk = Networks.Desktops(c);
        var egress = Networks.Egress(c);
        var user = UserChainFor(c);
        var input = InputChainFor(c);
        var r = new List<string>
        {
            $"-A DOCKER-USER -j {user}",
            $"-A INPUT -j {input}",
            $"-A {user} -i {bots.Bridge} -j DROP",
            $"-A {user} -o {bots.Bridge} -j DROP",
            $"-A {user} -i {desk.Bridge} -j DROP",
            $"-A {user} -o {desk.Bridge} -j DROP",
        };
        // Written in iptables-save's own option order (destination before interface) so a dump compares line for line.
        foreach (var range in Refused) r.Add($"-A {user} -d {range} -i {egress.Bridge} -j DROP");
        r.Add($"-A {input} -d {bots.Gateway}/32 -i {bots.Bridge} -p tcp -m tcp --dport {c.Gateway.Port} -j ACCEPT");
        if (c.HostExec.OpenFirewall) r.Add($"-A {input} -d {bots.Gateway}/32 -i {bots.Bridge} -p tcp -m tcp --dport 7476 -j ACCEPT");
        foreach (var bridge in new[] { bots.Bridge, desk.Bridge, egress.Bridge })
        {
            r.Add($"-A {input} -i {bridge} -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT");
            r.Add($"-A {input} -i {bridge} -j DROP");
        }
        return r;
    }

    /// <summary>A script Spencer runs once with sudo (oma-bot egress rules). It replaces only the roster's own chains.</summary>
    public static string Script(RosterConfig c)
    {
        var user = UserChainFor(c);
        var input = InputChainFor(c);
        var sb = new StringBuilder();
        sb.Append("#!/bin/sh\n# SPDX-License-Identifier: AGPL-3.0-or-later\n# Roster firewall rules (plan 7.7). Run with sudo after Docker starts; re-run after changing roster.json networks.\nset -eu\n");
        sb.Append($"iptables -N {user} 2>/dev/null || iptables -F {user}\n");
        sb.Append($"iptables -N {input} 2>/dev/null || iptables -F {input}\n");
        sb.Append($"iptables -C DOCKER-USER -j {user} 2>/dev/null || iptables -I DOCKER-USER 1 -j {user}\n");
        sb.Append($"iptables -C INPUT -j {input} 2>/dev/null || iptables -I INPUT 1 -j {input}\n");
        foreach (var rule in Rules(c).Skip(2)) sb.Append("iptables ").Append(rule).Append('\n');
        sb.Append("echo \"roster firewall rules applied\"\n");
        return sb.ToString();
    }

    /// <summary>Rules missing from an iptables-save dump of the filter table. Empty means every rule is in place.</summary>
    public static IReadOnlyList<string> Missing(RosterConfig c, string iptablesSave)
    {
        var have = new HashSet<string>(iptablesSave.Split('\n').Select(l => Normalize(l)), StringComparer.Ordinal);
        return [.. Rules(c).Where(rule => !have.Contains(Normalize(rule)))];
    }

    private static string Normalize(string line) => string.Join(' ', line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Reads the host's filter table. The supervisor uses it before every container start.</summary>
public interface IFirewallReader
{
    /// <summary>The iptables-save text of the filter table, or the plain reason it could not be read.</summary>
    Task<(string? Rules, string? Problem)> ReadAsync(CancellationToken ct);
}

/// <summary>
/// iptables-save, run directly when the supervisor may, else through a
/// passwordless sudo rule for exactly `iptables-save -t filter`.
/// </summary>
public sealed class IptablesSaveReader : IFirewallReader
{
    public async Task<(string? Rules, string? Problem)> ReadAsync(CancellationToken ct)
    {
        foreach (var (file, args) in new[] { ("iptables-save", new[] { "-t", "filter" }), ("sudo", new[] { "-n", "iptables-save", "-t", "filter" }) })
        {
            try
            {
                var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p is null) continue;
                var output = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
                if (p.ExitCode == 0 && output.Contains("*filter", StringComparison.Ordinal)) return (output, null);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not installed; try the next way.
            }
        }
        return (null, "the firewall rules could not be read (run the supervisor with iptables-save access, or add the sudo rule in docs/egress.md)");
    }
}

/// <summary>Firewall state for tests and dry runs.</summary>
public sealed class FixedFirewallReader(string? rules, string? problem = null) : IFirewallReader
{
    public string? Rules { get; set; } = rules;

    public Task<(string? Rules, string? Problem)> ReadAsync(CancellationToken ct) => Task.FromResult((Rules, Rules is null ? problem ?? "no rules" : null));
}

/// <summary>What the supervisor enforces about networks and the firewall before it starts any container with a network.</summary>
public sealed class NetworkPolicy(RosterConfig config, IFirewallReader firewall)
{
    public RosterConfig Config => config;

    /// <summary>Null when containers may start; otherwise why not.</summary>
    public async Task<string?> RefusalAsync(CancellationToken ct)
    {
        if (config.Egress.FirewallCheck == "off") return null;
        var (rules, problem) = await firewall.ReadAsync(ct).ConfigureAwait(false);
        if (rules is null) return "Refused to start: " + problem + ". Fail closed: no container starts without the roster firewall rules.";
        var missing = FirewallRules.Missing(config, rules);
        return missing.Count == 0 ? null : $"Refused to start: {missing.Count} roster firewall rule(s) are missing (first: {missing[0]}). Apply them with: oma-bot egress rules | sudo sh";
    }
}
