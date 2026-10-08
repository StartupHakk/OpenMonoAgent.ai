using System.Diagnostics;

namespace OpenMono.Windows.AgentHost;

/// <summary>
/// Resolves Node, npx, python, and uvx on Windows and wraps .cmd shims with
/// cmd.exe /d /c before handing McpServerConfig values to OMA. OMA McpClient
/// is not edited; the fix lives in how windows/ constructs the config.
/// </summary>
public static class McpLaunchHelper
{
    public sealed record McpLaunch(string FileName, string ArgumentsPrefix, string? WorkingDirectory);

    public static (string Command, string[] Args, string? WorkingDirectory) Resolve(
        string command,
        string[]? args,
        string? workingDirectory)
    {
        args ??= [];
        if (IsCmdShim(command))
        {
            var resolved = ResolveOnPath(command) ?? command;
            var quotedArgs = string.Join(' ', new[] { Quote(resolved) }.Concat(args.Select(Quote)));
            return ("cmd.exe", ["/d", "/c", quotedArgs], workingDirectory);
        }

        if (!Path.IsPathRooted(command))
        {
            var resolved = ResolveOnPath(command)
                ?? ResolveKnownLocation(command);
            if (resolved is not null)
            {
                return (resolved, args, workingDirectory);
            }
        }

        return (command, args, workingDirectory);
    }

    public static bool IsCmdShim(string command) =>
        command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
        || command.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
        || command.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);

    public static string? ResolveOnPath(string command)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!process.Start())
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5_000);
            if (process.ExitCode != 0)
            {
                return null;
            }

            return output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        }
        catch
        {
            return null;
        }
    }

    public static string? ResolveKnownLocation(string command)
    {
        var lower = command.ToLowerInvariant();
        string[] roots =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python"),
        ];
        foreach (var root in roots)
        {
            foreach (var ext in (lower is "node" or "npx") ? new[] { ".exe", ".cmd" } : new[] { ".exe" })
            {
                var candidate = Path.Combine(root, command + ext);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    internal static string Quote(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? $"\"{value}\"" : value;
}
