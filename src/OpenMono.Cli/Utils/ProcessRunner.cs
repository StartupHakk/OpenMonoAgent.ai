using System.ComponentModel;
using System.Diagnostics;

namespace OpenMono.Utils;

public static class ProcessRunner
{
    public static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string command,
        string? workingDirectory = null,
        int timeoutMs = 30_000,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            ArgumentList = { "-c", command },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        // Fail-open on start failures (missing /bin/bash on Windows, invalid working directory):
        // callers like GitHelper and the template engine treat this like any failed command.
        Process? process = null;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return (127, "", $"failed to start process: {ex.Message}");
        }

        if (process is null)
            return (127, "", "failed to start process");

        using (process)
        {
            var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderr = await process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);

            return (process.ExitCode, stdout.TrimEnd(), stderr.TrimEnd());
        }
    }
}
