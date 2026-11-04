// SPDX-License-Identifier: AGPL-3.0-or-later
// The fleet node agent (plan 7.21, phase 12). It runs on a fleet node beside the
// node's own OMA agent, only after Spencer invited and approved the node.
//   oma-bot-node enroll --controller https://NAME:PORT --relay HOST --ca-fingerprint FP --node NODE   (code on stdin)
//   oma-bot-node run --relay HOST [--agent http://127.0.0.1:7475] [--once]
//   oma-bot-node ledger
using System.Net.Sockets;
using Omabot.Acp;
using Omabot.Common;
using Omabot.Fleet;

var home = Environment.GetEnvironmentVariable("OMABOT_NODE_HOME") is { Length: > 0 } h ? h : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".omabot-node");
var rest = args.ToList();
string? Option(string name)
{
    var i = rest.IndexOf(name);
    if (i < 0 || i + 1 >= rest.Count) return null;
    var v = rest[i + 1];
    rest.RemoveRange(i, 2);
    return v;
}
bool Flag(string name) => rest.Remove(name);

// The node dials the relay host while TLS checks the controller's own name.
Func<HttpMessageHandler, HttpMessageHandler> Via(string? relay) => handler =>
{
    if (relay is not null && handler is SocketsHttpHandler s)
        s.ConnectCallback = async (ctx, ct) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(relay, ctx.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        };
    return handler;
};

try
{
    switch (rest.FirstOrDefault())
    {
        case "enroll":
            {
                rest.RemoveAt(0);
                var controller = Option("--controller") ?? throw new ArgumentException("--controller is required.");
                var fingerprint = Option("--ca-fingerprint") ?? throw new ArgumentException("--ca-fingerprint is required.");
                var node = Option("--node") ?? throw new ArgumentException("--node is required.");
                var relay = Option("--relay");
                var code = Console.IsInputRedirected ? Console.In.ReadLine()?.Trim() : null;
                if (string.IsNullOrEmpty(code)) throw new ArgumentException("Give the one-time code on standard input.");
                var id = await NodeAgent.EnrollAsync(new Uri(new Uri(controller), "/fleet/v1/enroll"), fingerprint, node, code, home, Via(relay));
                Console.WriteLine($"Enrolled {id.Name} in slot {id.Slot}. Ask Spencer to approve this key fingerprint:");
                Console.WriteLine(FleetCa.Fingerprint(id.Certificate));
                return 0;
            }
        case "run":
            {
                rest.RemoveAt(0);
                var relay = Option("--relay");
                var agent = Option("--agent") ?? "http://127.0.0.1:7475";
                var once = Flag("--once");
                var controllerName = Option("--controller-name") ?? "oma-fleet-controller";
                var basePort = int.TryParse(Option("--controller-port"), out var p) ? p : 7490;
                var id = NodeAgent.Load(home);
                var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                var runner = new ReadOnlyStepRunner(() => new AcpClient(http, agent));
                var nodeAgent = new NodeAgent(id, new Uri($"https://{controllerName}:{basePort + id.Slot}"), runner, home, Via(relay));
                using var stop = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
                do
                {
                    try
                    {
                        var job = await nodeAgent.RunOnceAsync(stop.Token);
                        if (job is not null) Console.WriteLine("worked on " + job);
                    }
                    catch (HttpRequestException ex)
                    {
                        Console.Error.WriteLine("controller not reachable: " + Redactor.Shared.Redact(ex.Message));
                    }
                    if (!once) await Task.Delay(TimeSpan.FromSeconds(5), stop.Token).ContinueWith(_ => { });
                }
                while (!once && !stop.IsCancellationRequested);
                return 0;
            }
        case "ledger":
            foreach (var line in new NodeAgent(NodeAgent.Load(home), new Uri("https://unused"), new ReadOnlyStepRunner(() => throw new InvalidOperationException()), home).Ledger())
                Console.WriteLine(line);
            return 0;
        default:
            Console.Error.WriteLine("Use: oma-bot-node enroll | run | ledger. Fleet mode is optional and off unless Spencer turned it on.");
            return 2;
    }
}
catch (Exception ex) when (ex is ArgumentException or FleetException or IOException or HttpRequestException)
{
    Console.Error.WriteLine(Redactor.Shared.Redact(ex.Message));
    return 1;
}
