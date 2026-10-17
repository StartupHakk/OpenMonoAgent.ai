using System.Diagnostics;
using FluentAssertions;

namespace OpenMono.Tests.Engine;

public class EnginePythonTests
{
    [Fact]
    public void PythonUnittestSuite_Passes()
    {
        var repo = FindRepoRoot();
        repo.Should().NotBeNull("OpenMono.sln + engine/ should be reachable from the test output dir");
        var engineDir = Path.Combine(repo!, "engine");
        Directory.Exists(engineDir).Should().BeTrue();

        var psi = new ProcessStartInfo
        {
            FileName = "python3",
            Arguments = "-m unittest discover -s tests -v",
            WorkingDirectory = engineDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["PYTHONPATH"] = engineDir;

        using var proc = Process.Start(psi);
        proc.Should().NotBeNull();
        var stdout = proc!.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(120_000).Should().BeTrue("engine tests should finish quickly");
        proc.ExitCode.Should().Be(0, $"python engine tests failed:\n{stdout}\n{stderr}");
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var sln = Path.Combine(dir.FullName, "OpenMono.sln");
            var engine = Path.Combine(dir.FullName, "engine");
            if (File.Exists(sln) && Directory.Exists(engine))
                return dir.FullName;
            dir = dir.Parent;
        }

        dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            var sln = Path.Combine(dir.FullName, "OpenMono.sln");
            var engine = Path.Combine(dir.FullName, "engine");
            if (File.Exists(sln) && Directory.Exists(engine))
                return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }
}
