using System.Diagnostics;

namespace OpenMono.Win.Inference;

// Starts and stops the long-lived llama-server. Behind an interface so tests
// drive the launcher without spawning anything.
public interface IServerProcessHost
{
    int Start(string executable, IReadOnlyList<string> arguments, string workingDirectory);

    bool IsRunning(int pid);

    void Stop(int pid);

    // llama-server processes whose executable lives under the directory: the
    // ones OpenMono installed, even when the state file is gone.
    IReadOnlyList<int> FindUnder(string directory);

    // Output of `llama-server --list-devices`, or null when it could not run
    // to the end.
    string? ListDevices(string executable, string workingDirectory);
}

// llama-server must outlive `openmono-win inference up`, and openmono.ps1
// captures this tool's output. A child that inherits the console handles
// would hold that pipe open and hang PowerShell, so it is started through the
// shell (no inherited handles) with a hidden window; its log goes to
// --log-file instead.
public sealed class ServerProcessHost : IServerProcessHost
{
    public int Start(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(executable, LlamaServerArgs.ToCommandLine(arguments))
        {
            UseShellExecute = OperatingSystem.IsWindows(),
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        if (!OperatingSystem.IsWindows())
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {executable}.");
        if (!OperatingSystem.IsWindows())
        {
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        return process.Id;
    }

    public bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited
                && process.ProcessName.StartsWith("llama-server", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public IReadOnlyList<int> FindUnder(string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var pids = new List<int>();
        foreach (var process in Process.GetProcessesByName("llama-server"))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } exe
                        && Path.GetFullPath(exe).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        pids.Add(process.Id);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    // Exited, or owned by another user: not ours to manage.
                }
            }
        }

        return pids;
    }

    public string? ListDevices(string executable, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(executable, LlamaDevices.Arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return stdout.Result + Environment.NewLine + stderr.Result;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public void Stop(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }
}
