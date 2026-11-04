// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Common;
using Omabot.Egress;

namespace Omabot.Cli;

public sealed partial class CliApp
{
    /// <summary>The firewall reader `egress check` uses; tests swap it.</summary>
    internal IFirewallReader FirewallReader { get; init; } = new IptablesSaveReader();

    private async Task<int> EgressCmd(Args a, CancellationToken ct)
    {
        var sub = a.NextOrNull() ?? "status";
        var config = LoadConfig();
        var clients = new EgressClients(env.Paths, config, Redactor.Shared);
        switch (sub)
        {
            case "rules":
                a.NoMore();
                env.Out.Write(FirewallRules.Script(config));
                return Exit.Ok;
            case "check":
                {
                    a.NoMore();
                    var refusal = await new NetworkPolicy(config, FirewallReader).RefusalAsync(ct).ConfigureAwait(false);
                    (refusal is null ? env.Out : env.Err).WriteLine(refusal ?? "Roster firewall rules are in place.");
                    return refusal is null ? Exit.Ok : Exit.Failed;
                }
            case "log":
                {
                    var n = int.TryParse(a.Option("--lines"), out var lines) ? Math.Clamp(lines, 1, 10000) : 50;
                    var refusedOnly = a.Flag("--refused");
                    a.NoMore();
                    var entries = EgressLog.Read(clients.LogPath).Where(e => !refusedOnly || e.Decision == "refuse").TakeLast(n).ToList();
                    if (entries.Count == 0) env.Out.WriteLine("No egress decisions yet.");
                    foreach (var e in entries) env.Out.WriteLine($"{e.At}  {e.Decision,-6} {e.Client,-20} {e.Host}:{e.Port}  {e.Reason}");
                    return Exit.Ok;
                }
            case "status":
                {
                    a.NoMore();
                    var (_, _, _, bots) = OpenDb();
                    env.Out.WriteLine($"Egress gate: {(config.Egress.Enabled ? "on" : "off")}, {config.Egress.Host}:{config.Egress.Port}, ports {string.Join(", ", config.Egress.Ports)}.");
                    foreach (var n in Networks.All(config)) env.Out.WriteLine($"Network {n.Name}: {n.Subnet} on {n.Bridge}{(n.Internal ? ", internal" : ", the way out")}.");
                    env.Out.WriteLine(File.Exists(clients.PolicyPath) ? $"Policy: {clients.PolicyPath}" : "Policy: not written yet (oma-bot serve writes it).");
                    var since = env.Clock.UtcNow.AddHours(-1).ToString("O");
                    var recent = EgressLog.Read(clients.LogPath).Where(e => string.CompareOrdinal(e.At, since) >= 0).ToList();
                    env.Out.WriteLine($"Last hour: {recent.Count(e => e.Decision == "allow")} allowed, {recent.Count(e => e.Decision == "refuse")} refused.");
                    foreach (var b in bots.All().Where(b => b.Enabled && !b.Spec.IsReadOnlyRole))
                        env.Out.WriteLine($"  {b.Name}: {(b.Spec.AllowHosts.Count == 0 ? "no hosts (search and scrape go through the gateway)" : string.Join(", ", b.Spec.AllowHosts))}");
                    return Exit.Ok;
                }
            default:
                throw new ArgsException("Use: oma-bot egress status | rules | check | log [--lines N] [--refused]");
        }
    }

    private async Task<int> DesktopsCmd(Args a, CancellationToken ct)
    {
        var sub = a.NextOrNull() ?? "list";
        var config = LoadConfig();
        var (db, audit, _, _) = OpenDb();
        var desktops = new Desktops.DesktopManager(db, config, env.Paths, env.Clock, audit);
        switch (sub)
        {
            case "list":
                {
                    a.NoMore();
                    var all = desktops.All();
                    if (all.Count == 0) env.Out.WriteLine("No desktops. Set desktop: own, shared or pool for a bot in the team file.");
                    foreach (var d in all)
                    {
                        var lease = desktops.LeaseOf(d.Name);
                        env.Out.WriteLine($"{d.Name,-16} {d.Mode,-7} {d.State,-8} 127.0.0.1:{d.Port}  {(lease is null ? "free" : $"leased to {lease.Bot} until {lease.ExpiresAt:HH:mm} UTC")}");
                    }
                    return Exit.Ok;
                }
            case "show":
                {
                    var name = a.Next("desktops show NAME");
                    a.NoMore();
                    var d = desktops.Get(name) ?? throw new ArgsException($"No desktop {name}.");
                    var password = new Desktops.DesktopSecrets(env.Paths, Redactor.Shared).Ensure(d.Name);
                    env.Out.WriteLine($"{d.Name} ({d.Mode}{(d.Owner is null ? "" : ", " + d.Owner)}) is {d.State}.");
                    env.Out.WriteLine($"Watch it at http://127.0.0.1:{d.Port}/vnc.html?view_only=1 (this computer only).");
                    // Spencer asked for it on his own terminal; the view-only password is never logged or audited.
                    env.Out.WriteLine("View-only password: " + password);
                    return Exit.Ok;
                }
            case "stop":
                {
                    var name = a.Next("desktops stop NAME");
                    a.NoMore();
                    var d = desktops.Get(name) ?? throw new ArgsException($"No desktop {name}.");
                    await RequireSupervisor().StopServiceAsync(d.Container, ct).ConfigureAwait(false);
                    desktops.SetState(d.Name, "stopped");
                    if (desktops.LeaseOf(d.Name) is { } l) desktops.Release(l.Bot, l.AssignmentId, "stopped by Spencer");
                    audit.Write(PersonAtCli.ToString(), "desktop.stop", d.Name, "by hand");
                    env.Out.WriteLine($"Stopped {d.Name}.");
                    return Exit.Ok;
                }
            default:
                throw new ArgsException("Use: oma-bot desktops [list] | show NAME | stop NAME");
        }
    }
}
