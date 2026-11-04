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
var app = SupervisorHost.Build(paths.SupervisorSocket, token, new DirectSupervisor(engine, config.Bots.Network), Console.WriteLine);
Console.WriteLine($"Supervisor listening on {paths.SupervisorSocket}.");
await app.RunAsync().ConfigureAwait(false);
return 0;
