using System.Text;
using OpenMono.Windows.Hardware;

namespace OpenMono.Windows.Desktop.Services;

/// <summary>
/// One click diagnostics bundle: redacted settings, server logs tail,
/// hardware report, and version info for bug reports.
/// </summary>
public static class DiagnosticsService
{
    public static async Task<string> WriteBundleAsync(AppState state, CancellationToken ct = default)
    {
        var dir = Path.Combine(Path.GetTempPath(), "openmono-diag");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"openmono-diag-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt");
        var sb = new StringBuilder();
        sb.AppendLine($"OpenMono for Windows diagnostics {DateTime.UtcNow:O}");
        sb.AppendLine($"App version: {ReadAppVersion()}");
        sb.AppendLine($"Endpoint: {state.Supervisor.LlamaEndpoint}");
        if (state.Supervisor.AllowLanConnections)
        {
            sb.AppendLine($"LAN serving: on ({string.Join(", ", state.Supervisor.LanAdvertisedUrls())})");
        }
        sb.AppendLine($"Models dir: {state.Supervisor.ModelsDirectory}");
        sb.AppendLine($"Workspace: {state.Workspace}");
        if (state.Hardware is { } hw)
        {
            sb.AppendLine($"Tier: {(int)hw.Selection.Tier} flavor: {hw.Selection.Flavor} cuda: {hw.CudaAvailable}");
            foreach (var gpu in hw.Gpus)
            {
                sb.AppendLine($"GPU: {gpu.Name} vendor={gpu.Vendor} dedicated={gpu.DedicatedBytes} driver={gpu.DriverVersion}");
            }

            sb.AppendLine($"RAM: {hw.Memory.TotalRamBytes} cores: {hw.Memory.PhysicalCores}/{hw.Memory.LogicalCores}");
            foreach (var warning in hw.Selection.Warnings)
            {
                sb.AppendLine($"Warn: {warning}");
            }
        }

        sb.AppendLine("Settings (api keys redacted):");
        sb.AppendLine(RedactedSettings(state.Supervisor.DataDirectory));
        sb.AppendLine("llama-server log tail:");
        sb.AppendLine(ServerLogTail(state));
        await File.WriteAllTextAsync(path, sb.ToString(), ct);
        return path;
    }

    private static string ReadAppVersion()
    {
        try
        {
            var root = FindWindowsRoot();
            if (root is not null)
            {
                var versionFile = Path.Combine(root, "VERSION.windows");
                if (File.Exists(versionFile))
                {
                    return File.ReadAllText(versionFile).Trim();
                }
            }
        }
        catch
        {
        }

        return "unknown";
    }

    private static string? FindWindowsRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            if (File.Exists(Path.Combine(dir, "VERSION.windows")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return null;
    }

    private static string RedactedSettings(string dataDirectory)
    {
        try
        {
            var path = Path.Combine(dataDirectory, "settings.json");
            if (!File.Exists(path))
            {
                return "(no settings.json)";
            }

            var text = File.ReadAllText(path);
            return System.Text.RegularExpressions.Regex.Replace(
                text,
                "\"api_key\"\\s*:\\s*\"[^\"]*\"",
                "\"api_key\": \"(redacted)\"",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        catch (Exception ex)
        {
            return $"(cannot read settings: {ex.Message})";
        }
    }

    private static string ServerLogTail(AppState state)
    {
        try
        {
            var log = state.Llama?.LogPath;
            if (log is null || !File.Exists(log))
            {
                var latest = Directory.Exists(state.Supervisor.LogsDirectory)
                    ? Directory.GetFiles(state.Supervisor.LogsDirectory, "llama-server-*.log").OrderDescending().FirstOrDefault()
                    : null;
                if (latest is null)
                {
                    return "(no server log)";
                }

                log = latest;
            }

            var lines = File.ReadAllLines(log);
            return string.Join('\n', lines.TakeLast(80));
        }
        catch (Exception ex)
        {
            return $"(cannot read log: {ex.Message})";
        }
    }
}
