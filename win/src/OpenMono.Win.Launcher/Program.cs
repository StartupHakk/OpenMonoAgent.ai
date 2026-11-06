using OpenMono.Win.Foundry;
using OpenMono.Win.Launcher;

// openmono-win: decision logic behind win/openmono.ps1. PowerShell owns the
// user-facing commands; this tool answers "is inference up, what should
// docker run, is the machine ready" with output PowerShell can parse.
//
//   openmono-win inference up [--model ALIAS] [--port N] [--json]
//   openmono-win inference down [--force]
//   openmono-win inference status [--json]
//   openmono-win agent-plan --workdir DIR --compose FILE [--vision] [--model ALIAS] [--no-git-auth] -- <agent args>
//   openmono-win doctor [--workdir DIR] [--json]

var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
var deps = new LauncherDeps(
    new ProcessCliRunner(),
    http,
    new SystemGpuProber(new ProcessCliRunner()),
    homeDir,
    Environment.GetEnvironmentVariable);

try
{
    return await LauncherCli.RunAsync(args, deps, Console.Out, Console.Error);
}
catch (OperationCanceledException)
{
    return 130;
}
