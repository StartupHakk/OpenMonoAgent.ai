using System.Diagnostics;
using System.Text.Json;
using OpenMono.Permissions;
using OpenMono.Tools;

namespace OpenMono.Windows.AgentHost;

/// <summary>
/// PowerShell first replacement for OMA BashTool. Registered under the same
/// "Bash" name BEFORE any OMA registration so it wins without editing OMA.
/// Accepts the identical schema (command, timeout_ms, background) and runs via
/// PowerShell 7 (pwsh.exe) when present, else Windows PowerShell, else cmd.exe.
/// Background jobs use the temp scratch dir with Windows style follow ups.
/// </summary>
public sealed class WindowsShellTool : ToolBase
{
    public override string Name => "Bash";

    public override string Description =>
        "Execute a shell command. The working directory persists between calls. " +
        "Use for git, build tools, and other system operations. " +
        "On Windows, commands run in PowerShell 7 (pwsh.exe) when installed, else Windows PowerShell, else cmd.exe. " +
        "Prefer native commands (git, dotnet, npm, rg) and PowerShell syntax. " +
        "Commands run NON-INTERACTIVELY: stdin is closed, so always pass non-interactive flags. " +
        "For long-running processes that do not exit on their own set background=true, which spawns " +
        "the process detached, writes stdout and stderr to a log file under the temp scratch dir, " +
        "and returns the PID plus log path immediately so the conversation can continue.";

    protected override SchemaBuilder DefineSchema() => new SchemaBuilder()
        .AddProperty("command", new { type = "string", minLength = 1, description = "The shell command to execute (PowerShell syntax preferred)" })
        .AddInteger("timeout_ms", "Timeout in milliseconds (default: 300000, max: 600000). Ignored when background=true.", minimum: 1, maximum: 600000)
        .AddBoolean("background", "If true, launch the process detached, write stdout+stderr to a log file under the temp scratch dir, and return the PID + log path immediately. Use for servers, watchers, or anything that never exits on its own.")
        .Require("command");

    public override PermissionLevel RequiredPermission(JsonElement input)
    {
        var command = input.TryGetProperty("command", out var cmd) ? cmd.GetString() ?? string.Empty : string.Empty;
        if (WindowsGuardrails.IsDestructive(command))
        {
            return PermissionLevel.Deny;
        }

        return SanityCheck.IsDestructiveCommand(command) ? PermissionLevel.Deny : PermissionLevel.Ask;
    }

    public override IReadOnlyList<Capability> RequiredCapabilities(JsonElement input)
    {
        var command = input.TryGetProperty("command", out var cmd) ? cmd.GetString() : null;
        if (string.IsNullOrWhiteSpace(command))
        {
            return [];
        }

        // Reuse the OMA bash parser for capability mapping so permission rules
        // keep working. The binary reported is the selected shell.
        var parseResult = BashParser.Parse(command);
        var caps = new List<Capability>(BashParser.ToCapabilities(parseResult));
        return caps;
    }

    protected override async Task<ToolResult> ExecuteCoreAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        var command = input.GetProperty("command").GetString()!;
        var background = input.TryGetProperty("background", out var b)
            && b.ValueKind == JsonValueKind.True;

        var shell = HookShellSelector.SelectShell();
        if (background)
        {
            return RunBackground(command, shell, context);
        }

        var timeoutMs = input.TryGetProperty("timeout_ms", out var t) ? t.GetInt32() : 300_000;
        if (timeoutMs <= 0)
        {
            timeoutMs = 300_000;
        }

        timeoutMs = Math.Min(timeoutMs, 600_000);

        var psi = BuildProcess(shell, command, context.WorkingDirectory);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Failed to start process: {ex.Message}");
        }

        if (process is null)
        {
            return ToolResult.Error($"Failed to start process for command: {command}");
        }

        try
        {
            process.StandardInput.Close();
        }
        catch
        {
        }

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                await KillProcessTreeAsync(process);
                if (ct.IsCancellationRequested)
                {
                    return ToolResult.Error($"Command cancelled by user: {command}");
                }

                return ToolResult.Error(
                    $"Command timed out after {timeoutMs}ms and was terminated (entire process tree killed): {command}");
            }
            catch (Exception ex)
            {
                await KillProcessTreeAsync(process);
                return ToolResult.Error($"Failed while awaiting process: {ex.Message}");
            }

            string stdout = string.Empty;
            string stderr = string.Empty;
            try
            {
                stdout = await stdoutTask;
                stderr = await stderrTask;
            }
            catch (OperationCanceledException)
            {
            }

            var output = new List<string>();
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                output.Add(stdout.TrimEnd());
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                output.Add($"[stderr]\n{stderr.TrimEnd()}");
            }

            var content = output.Count > 0 ? string.Join('\n', output) : "(no output)";
            if (process.ExitCode != 0)
            {
                content = $"Exit code: {process.ExitCode}\n{content}";
            }

            const int maxLength = 50_000;
            if (content.Length > maxLength)
            {
                content = content[..maxLength] + $"\n... (truncated, {content.Length} total chars)";
            }

            return process.ExitCode == 0
                ? ToolResult.Success(content)
                : ToolResult.Error(content);
        }
    }

    private static ToolResult RunBackground(string command, HookShellSelector.ShellKind shell, ToolContext context)
    {
        var bgDir = Path.Combine(Path.GetTempPath(), "openmono", "bg");
        try
        {
            Directory.CreateDirectory(bgDir);
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Failed to create background log directory {bgDir}: {ex.Message}");
        }

        var logName = $"bg-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.log";
        var logPath = Path.Combine(bgDir, logName);

        // Redirect inside the target shell so logging works on every shell kind.
        string wrapped = shell switch
        {
            HookShellSelector.ShellKind.Cmd => $"/d /c ({command}) >> \"{logPath}\" 2>&1",
            _ => $"-NoProfile -NonInteractive -Command \"& {{ {command} }} *>> '{logPath}'\"",
        };

        var psi = shell == HookShellSelector.ShellKind.Cmd
            ? new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = wrapped,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = context.WorkingDirectory,
            }
            : new ProcessStartInfo
            {
                FileName = HookShellSelector.ShellExecutable(shell),
                Arguments = wrapped,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = context.WorkingDirectory,
            };

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"Failed to start background process: {ex.Message}");
        }

        if (process is null)
        {
            return ToolResult.Error($"Failed to start background process for command: {command}");
        }

        var pid = process.Id;
        var summary =
            $"Started in background, PID {pid}\n" +
            $"Log: {logPath}\n" +
            "\n" +
            "Follow-ups (run foreground):\n" +
            $"  Get-Content -Tail 50 '{logPath}'   # peek at output\n" +
            $"  Stop-Process -Id {pid}              # stop the process\n" +
            $"  Stop-Process -Id {pid} -Force       # force stop if it will not stop\n";
        return ToolResult.Success(summary);
    }

    /// <summary>
    /// Builds the foreground ProcessStartInfo. Internal for unit tests.
    /// </summary>
    internal static ProcessStartInfo BuildProcess(HookShellSelector.ShellKind shell, string command, string workingDirectory)
    {
        if (shell == HookShellSelector.ShellKind.Cmd)
        {
            return new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c \"{command.Replace("\"", "\"\"", StringComparison.Ordinal)}\"",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory,
            };
        }

        return new ProcessStartInfo
        {
            FileName = HookShellSelector.ShellExecutable(shell),
            Arguments = $"-NoProfile -NonInteractive -Command \"{command.Replace("\"", "`\"", StringComparison.Ordinal)}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
    }

    internal static async Task KillProcessTreeAsync(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch
        {
        }

        try
        {
            using var graceCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(graceCts.Token);
        }
        catch
        {
        }
    }
}
