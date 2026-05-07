using System.Diagnostics;

namespace OpenMono.Utils;

public static class ShellHelper
{
    private static readonly Lazy<string> _shellPath = new(ResolveShell);

    public static string ShellPath => _shellPath.Value;

    public static void ConfigureProcessForShell(ProcessStartInfo psi, string command)
    {
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = ShellPath;
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.FileName = "/bin/bash";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
    }

    public static void SetShellEnvironment(ProcessStartInfo psi)
    {
        if (OperatingSystem.IsWindows())
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            psi.Environment["HOME"] = Environment.GetEnvironmentVariable("HOME") ?? userProfile;
            psi.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "";
        }
        else
        {
            psi.Environment["HOME"] = Environment.GetEnvironmentVariable("HOME") ?? "/root";
            psi.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/local/bin:/usr/bin:/bin";
        }
    }

    public static string GetHomeDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return Environment.GetEnvironmentVariable("HOME") ?? "/root";
    }

    private static string ResolveShell()
    {
        if (!OperatingSystem.IsWindows())
            return "/bin/bash";

        var gitBash = @"C:\Program Files\Git\bin\bash.exe";
        if (File.Exists(gitBash))
            return gitBash;

        var gitBashX86 = @"C:\Program Files (x86)\Git\bin\bash.exe";
        if (File.Exists(gitBashX86))
            return gitBashX86;

        var pathBash = FindOnPath("bash.exe");
        if (pathBash is not null)
            return pathBash;

        return "bash";
    }

    private static string? FindOnPath(string executable)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            var fullPath = Path.Combine(dir, executable);
            if (File.Exists(fullPath))
                return fullPath;
        }
        return null;
    }
}
