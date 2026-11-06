using System.Diagnostics;

namespace OpenMono.Win.Inference;

// Production ICliRunner that spawns real child processes with captured output.
// stdout and stderr are drained concurrently: reading one to the end before
// the other deadlocks once the unread pipe's buffer fills (model downloads
// print a lot of progress).
public sealed class ProcessCliRunner : ICliRunner
{
    public async Task<CliResult> RunAsync(string fileName, string arguments, CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited between the cancel and the kill.
            }

            throw;
        }

        return new CliResult(process.ExitCode, await stdoutTask, await stderrTask);
    }
}
