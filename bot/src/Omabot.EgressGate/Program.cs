// SPDX-License-Identifier: AGPL-3.0-or-later
// The roster egress gate (plan 7.7). Runs in its own container on the
// roster networks and the egress network. Reads the policy the roster
// writes (token hashes, allowlists, blocked hosts, ports) and reloads it
// when it changes. Refuses to start without a policy file.
using System.Net;
using Omabot.Common;
using Omabot.Egress;

var policyPath = Environment.GetEnvironmentVariable("OMABOT_EGRESS_POLICY") ?? "/etc/omabot-egress/policy.json";
var logPath = Environment.GetEnvironmentVariable("OMABOT_EGRESS_LOG") ?? "/var/log/omabot-egress/egress.log";
var listen = Environment.GetEnvironmentVariable("OMABOT_EGRESS_LISTEN") ?? "0.0.0.0:3128";
if (!File.Exists(policyPath))
{
    Console.Error.WriteLine($"oma-bot-egress: no policy at {policyPath}; refusing to start (every request would be refused anyway).");
    return 1;
}
if (!EgressProxy.TrySplitHostPort(listen, out var host, out var port) || !IPAddress.TryParse(host, out var ip))
{
    Console.Error.WriteLine($"oma-bot-egress: OMABOT_EGRESS_LISTEN \"{listen}\" is not address:port.");
    return 1;
}

var current = EgressPolicy.Parse(File.ReadAllText(policyPath));
var stamp = File.GetLastWriteTimeUtc(policyPath);
var gate = new Lock();
EgressPolicy Policy()
{
    lock (gate)
    {
        try
        {
            var now = File.GetLastWriteTimeUtc(policyPath);
            if (now != stamp)
            {
                current = EgressPolicy.Parse(File.ReadAllText(policyPath));
                stamp = now;
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            // Keep the last good policy; a half-written file never opens anything new.
        }
        return current;
    }
}

await using var proxy = new EgressProxy(Policy, DnsResolver.Instance, SocketConnector.Instance, new EgressLog(logPath, SystemClock.Instance));
proxy.Start(new IPEndPoint(ip, port));
Console.WriteLine($"oma-bot-egress listening on {listen} with {current.Clients.Count} client(s).");
var done = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => done.TrySetResult();
await done.Task;
return 0;
