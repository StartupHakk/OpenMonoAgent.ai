// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Cli;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
return await new CliApp(CliEnv.Default()).RunAsync(args, cts.Token).ConfigureAwait(false);
