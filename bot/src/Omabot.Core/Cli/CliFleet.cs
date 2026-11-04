// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using Omabot.Common;
using Omabot.Fleet;

namespace Omabot.Cli;

public sealed partial class CliApp
{
    internal FleetController FleetFor(Config.RosterConfig config)
    {
        var (db, audit, notices, _) = OpenDb();
        return new FleetController(db, config, env.Paths, env.Clock, audit, notices, FleetCa.LoadOrCreate(env.Paths, env.Clock));
    }

    /// <summary>
    /// `oma-bot fleet`: run by a person on the roster host, never a bot tool
    /// (plan 7.21, hard rule 19). Everything but enable refuses while fleet
    /// mode is off.
    /// </summary>
    private Task<int> FleetCmd(Args a, CancellationToken ct)
    {
        var sub = a.Next("fleet enable|disable|status|invite|approve|pause|resume|drain|revoke|job|jobs|secret");
        var config = LoadConfig();
        var who = PersonAtCli.ToString();
        var (_, audit, _, _) = OpenDb();
        switch (sub)
        {
            case "enable":
                {
                    var by = a.Option("--by") ?? throw new ArgsException("Give --by NAME: the person turning fleet mode on.");
                    var confirm = a.Flag("--confirm");
                    a.NoMore();
                    if (!confirm) throw new ArgsException("Fleet mode sends work to other hosts. Run again with --confirm if that is what you want.");
                    config.Placement.Mode = "fleet";
                    config.Fleet.EnabledBy = by.Trim();
                    config.Save(env.Paths);
                    audit.Write(who, "fleet.enabled", by, "placement.mode fleet");
                    var ca = FleetCa.LoadOrCreate(env.Paths, env.Clock);
                    env.Out.WriteLine($"Fleet mode is on, turned on by {by}. Fleet CA fingerprint: {FleetCa.Fingerprint(ca.Ca)}");
                    env.Out.WriteLine("Restart oma-bot serve to start the controller. Invite nodes with: oma-bot fleet invite NODE --labels role=scrape");
                    return Task.FromResult(Exit.Ok);
                }
            case "disable":
                a.NoMore();
                config.Placement.Mode = "one_host";
                config.Fleet.EnabledBy = null;
                config.Save(env.Paths);
                audit.Write(who, "fleet.disabled", "", "placement.mode one_host");
                env.Out.WriteLine("Fleet mode is off. Restart oma-bot serve to stop the controller.");
                return Task.FromResult(Exit.Ok);
        }
        var fleet = FleetFor(config);
        if (fleet.GuardProblem() is { } off) { env.Err.WriteLine(off); return Task.FromResult(Exit.Failed); }
        string Node() => a.Next($"fleet {sub} NODE");
        try
        {
            switch (sub)
            {
                case "status":
                    a.NoMore();
                    env.Out.WriteLine($"Fleet mode on (by {config.Fleet.EnabledBy}){(config.Fleet.KillSwitch ? ", PAUSED: no new leases" : "")}. CA {FleetCa.Fingerprint(fleet.Ca.Ca)[..16]}");
                    foreach (var n in fleet.Nodes())
                        env.Out.WriteLine($"  {n.Name,-16} slot {n.Slot,-2} port {config.Fleet.ControllerPort + n.Slot} {n.State,-9} {string.Join(",", n.Labels.Select(kv => kv.Key + "=" + kv.Value))}{(n.NeverTouch ? " never-touch" : "")} heartbeat {n.LastHeartbeat ?? "never"}");
                    foreach (var g in fleet.Jobs().GroupBy(j => j.State)) env.Out.WriteLine($"  jobs {g.Key}: {g.Count()}");
                    return Task.FromResult(Exit.Ok);
                case "invite":
                    {
                        var node = Node();
                        var labels = (a.Option("--labels") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "true", StringComparer.Ordinal);
                        var never = a.Flag("--never-touch");
                        a.NoMore();
                        var code = fleet.Invite(node, labels, never, who);
                        var slot = fleet.Node(node)!.Slot;
                        env.Out.WriteLine($"One-time code for {node} (15 minutes, works once): {code}");
                        env.Out.WriteLine($"Fleet CA fingerprint to pin: {FleetCa.Fingerprint(fleet.Ca.Ca)}");
                        env.Out.WriteLine($"Relay: expose controller port {config.Fleet.ControllerPort} for enrollment and {config.Fleet.ControllerPort + slot} for this node only.");
                        env.Out.WriteLine($"On {node}: oma-bot-node enroll --controller https://{config.Fleet.ControllerName}:{config.Fleet.ControllerPort} --ca-fingerprint FINGERPRINT --node {node} (the code on standard input)");
                        return Task.FromResult(Exit.Ok);
                    }
                case "approve":
                    {
                        var node = Node();
                        var fp = a.Option("--fingerprint") ?? throw new ArgsException("Give --fingerprint: the one oma-bot-node enroll printed on the node.");
                        a.NoMore();
                        fleet.Approve(node, fp, who);
                        env.Out.WriteLine($"{node} is approved and may lease work.");
                        return Task.FromResult(Exit.Ok);
                    }
                case "pause" or "resume":
                    a.NoMore();
                    config.Fleet.KillSwitch = sub == "pause";
                    config.Save(env.Paths);
                    audit.Write(who, "fleet." + sub, "", "");
                    env.Out.WriteLine(sub == "pause" ? "Fleet paused: no new leases. Running jobs finish or time out." : "Fleet resumed.");
                    return Task.FromResult(Exit.Ok);
                case "drain":
                    {
                        var node = Node();
                        a.NoMore();
                        fleet.Drain(node, who);
                        env.Out.WriteLine($"{node} is draining: it finishes its jobs and takes no new ones.");
                        return Task.FromResult(Exit.Ok);
                    }
                case "revoke":
                    {
                        var node = Node();
                        a.NoMore();
                        fleet.Revoke(node, who);
                        env.Out.WriteLine($"{node} is revoked: its certificate is refused and its leases ended. A new invite is needed to enroll it again.");
                        return Task.FromResult(Exit.Ok);
                    }
                case "jobs":
                    a.NoMore();
                    foreach (var j in fleet.Jobs()) env.Out.WriteLine($"{j.Id} {j.State,-16} {j.Kind,-14} {j.Key} {(j.Node is null ? "" : "on " + j.Node)} {(j.ApprovedBy is null ? "" : "approved by " + j.ApprovedBy)}");
                    return Task.FromResult(Exit.Ok);
                case "job":
                    {
                        var what = a.Next("fleet job add FILE | show ID | cancel ID");
                        var arg = a.Next($"fleet job {what} ...");
                        a.NoMore();
                        switch (what)
                        {
                            case "add":
                                {
                                    var text = File.ReadAllText(arg);
                                    var spec = (arg.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? JsonNode.Parse(text) : YamlToJson(text)) as JsonObject
                                        ?? throw new ArgsException("A job file is one JSON or YAML object.");
                                    var job = fleet.AddJob(spec, who);
                                    env.Out.WriteLine(job.State == "queued" ? $"Job {job.Id} queued under the read-only policy." : $"Job {job.Id} waits for your answer: oma-bot approvals show {job.ApprovalId}");
                                    return Task.FromResult(Exit.Ok);
                                }
                            case "show":
                                {
                                    var job = fleet.Job(arg) ?? throw new ArgsException($"No job {arg}.");
                                    env.Out.WriteLine($"{job.Id} {job.State} {job.Kind} {job.Key} attempts {job.Attempts} fencing {job.Fencing}{(job.Reason is null ? "" : " (" + job.Reason + ")")}");
                                    env.Out.WriteLine(job.Spec.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                                    return Task.FromResult(Exit.Ok);
                                }
                            case "cancel":
                                fleet.Cancel(arg, who);
                                env.Out.WriteLine($"Job {arg} cancelled.");
                                return Task.FromResult(Exit.Ok);
                            default:
                                throw new ArgsException("Use: oma-bot fleet job add FILE | show ID | cancel ID");
                        }
                    }
                case "secret":
                    {
                        if (a.Next("fleet secret set NAME") != "set") throw new ArgsException("Use: oma-bot fleet secret set NAME (value on standard input)");
                        var name = a.Next("fleet secret set NAME");
                        a.NoMore();
                        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9][a-z0-9._-]{0,63}$")) throw new ArgsException("Secret names are lower case letters, digits, dots, dashes and underscores.");
                        var value = (env.ReadStdin() ?? throw new ArgsException("Give the value on standard input.")).TrimEnd('\n', '\r');
                        if (value.Length == 0) throw new ArgsException("The value is empty.");
                        SecureFile.EnsureDir(FleetController.JobSecretDir(env.Paths));
                        SecureFile.WriteAllTextAtomic(Path.Combine(FleetController.JobSecretDir(env.Paths), name), value + "\n");
                        Redactor.Shared.AddKnownSecret(value);
                        audit.Write(who, "fleet.secret_set", name, "value stored 0600, never printed");
                        env.Out.WriteLine($"Stored job secret {name}.");
                        return Task.FromResult(Exit.Ok);
                    }
                default:
                    throw new ArgsException("Use: oma-bot fleet enable|disable|status|invite|approve|pause|resume|drain|revoke|job|jobs|secret");
            }
        }
        catch (FleetException ex)
        {
            env.Err.WriteLine(ex.Message);
            return Task.FromResult(Exit.Failed);
        }
    }

    private static JsonNode? YamlToJson(string yaml)
    {
        var obj = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<object>(yaml);
        var json = new YamlDotNet.Serialization.SerializerBuilder().JsonCompatible().Build().Serialize(obj);
        return JsonNode.Parse(json);
    }
}
