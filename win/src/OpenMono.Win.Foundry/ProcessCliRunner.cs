using System.Diagnostics;

namespace OpenMono.Win.Foundry;

// Production ICliRunner that spawns real child processes with captured output.
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

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new CliResult(1, string.Empty, $"Could not start {fileName}.");
            }

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return new CliResult(process.ExitCode, stdout, stderr);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException or OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Missing executable surfaces as Win32Exception on Windows; anything
            // else unexpected is wrapped so callers see which command failed.
            throw new InvalidOperationException($"Failed to run '{fileName} {arguments}': {ex.Message}", ex);
        }
    }
}
