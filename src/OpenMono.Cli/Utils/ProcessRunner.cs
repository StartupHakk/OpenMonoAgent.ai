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

        using var process = Process.Start(psi)!;
        var stdout = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderr = await process.StandardError.ReadToEndAsync(timeoutCts.Token);
        await process.WaitForExitAsync(timeoutCts.Token);

        return (process.ExitCode, stdout.TrimEnd(), stderr.TrimEnd());
    }
}
