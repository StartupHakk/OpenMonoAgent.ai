// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Common;
using Omabot.Config;
using Omabot.Containers;
using Omabot.Supervision;

// oma-bot-supervisor: holds the Docker socket, serves run/supervisor.sock.
var paths = new RosterPaths(RosterPaths.DefaultHome());
if (!File.Exists(paths.ConfigFile))
{
    Console.Error.WriteLine($"No roster at {paths.Home}. Run oma-bot init first.");
    return 1;
}
var config = RosterConfig.Load(paths);
SecureFile.EnsureDirNoClobber(paths.Run);
SecureFile.WriteIfMissing(paths.SupervisorToken, SecureFile.NewToken() + "\n");
var token = File.ReadAllText(paths.SupervisorToken).Trim();
Redactor.Shared.AddKnownSecret(token);
var socket = Environment.GetEnvironmentVariable("DOCKER_SOCKET") ?? "/var/run/docker.sock";
using var engine = new DockerEngineClient(socket);
if (!await engine.PingAsync().ConfigureAwait(false))
{
    Console.Error.WriteLine($"Docker does not answer on {socket}.");
    return 1;
}
// Check containers may mount only bot folders, the allowed workspace roots and the installed checks folder.
var appDir = Environment.GetEnvironmentVariable("OMABOT_APP") ?? Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('/')) ?? AppContext.BaseDirectory;
var checkRoots = new List<string> { paths.Bots, Path.Combine(appDir, "checks") };
checkRoots.AddRange(config.WorkspaceRoots);
// Every networked start needs the roster firewall rules (plan 7.7, fail closed); the gate and desktops mount only under the roster home.
var policy = new Omabot.Egress.NetworkPolicy(config, new Omabot.Egress.IptablesSaveReader());
var app = SupervisorHost.Build(paths.SupervisorSocket, token, new DirectSupervisor(engine, config.Bots.Network, checkRoots, policy, [paths.Home], config.Bots.Tenant), Console.WriteLine);
Console.WriteLine($"Supervisor listening on {paths.SupervisorSocket}.");
await app.RunAsync().ConfigureAwait(false);
return 0;
