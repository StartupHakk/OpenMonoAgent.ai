using OpenMono.Win.Inference;
using OpenMono.Win.Launcher;

// openmono-win: decision logic behind win/openmono.ps1. PowerShell owns the
// user-facing commands; this tool answers "is inference up, what should
// docker run, is the machine ready" with output PowerShell can parse.
//
//   openmono-win inference up [--model ALIAS] [--port N] [--update] [--json]
//   openmono-win inference down [--force]
//   openmono-win inference status [--json]
//   openmono-win agent-plan --workdir DIR --compose FILE [--vision] [--model ALIAS] [--no-git-auth] -- <agent args>
//   openmono-win doctor [--workdir DIR] [--json]
//   openmono-win diag [--out FILE.zip] [--workdir DIR]
//   openmono-win models [--json] | models check

var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
using var downloads = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
var deps = new LauncherDeps(
    new ProcessCliRunner(),
    http,
    new SystemGpuProber(new ProcessCliRunner()),
    homeDir,
    Environment.GetEnvironmentVariable,
    new ServerProcessHost(),
    Progress: Console.Error,
    DownloadHttp: downloads);

try
{
    return await LauncherCli.RunAsync(args, deps, Console.Out, Console.Error);
}
catch (OperationCanceledException)
{
    return 130;
}
