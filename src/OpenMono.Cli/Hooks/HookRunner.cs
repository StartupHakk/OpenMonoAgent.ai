using System.Diagnostics;
using OpenMono.Config;

namespace OpenMono.Hooks;

public sealed record HookDecision(bool Allowed, string? Reason)
{
    public static readonly HookDecision Allow = new(true, null);
}

public sealed class HookRunner
{
    private const int BlockExitCode = 2;

    private readonly AppConfig _config;
    private readonly Action<string>? _warn;

    public HookRunner(AppConfig config, Action<string>? warn = null)
    {
        _config = config;
        _warn = warn;
    }

    public async Task RunSessionStartHooksAsync(CancellationToken ct)
    {
        foreach (var hook in _config.Hooks.SessionStart)
        {
            await ExecuteHookAsync(hook, new Dictionary<string, string>(), ct);
        }
    }

    public async Task<HookDecision> RunPreToolUseHooksAsync(
        string toolName, string toolInput, CancellationToken ct)
    {
        var vars = new Dictionary<string, string>
        {
            ["tool_name"] = toolName,
            ["tool_input"] = toolInput,
        };

        foreach (var hook in _config.Hooks.PreToolUse)
        {
            if (hook.Condition is not null)
            {
                if (hook.Condition.Tool is not null &&
                    !hook.Condition.Tool.Equals(toolName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (hook.Condition.InputContains is not null &&
                    !toolInput.Contains(hook.Condition.InputContains, StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            var exec = await ExecuteHookAsync(hook, vars, ct);
            if (exec.ExitCode == BlockExitCode)
            {
                var reason =
                    !string.IsNullOrWhiteSpace(exec.Stderr) ? exec.Stderr.Trim()
                    : !string.IsNullOrWhiteSpace(exec.Stdout) ? exec.Stdout.Trim()
                    : "Blocked by a PreToolUse hook";
                return new HookDecision(false, reason);
            }
        }

        return HookDecision.Allow;
    }

    public async Task RunPostToolUseHooksAsync(
        string toolName, string toolOutput, CancellationToken ct)
    {
        var vars = new Dictionary<string, string>
        {
            ["tool_name"] = toolName,
            ["tool_output"] = toolOutput,
        };

        foreach (var hook in _config.Hooks.PostToolUse)
        {
            if (hook.Condition is not null &&
                hook.Condition.Tool is not null &&
                !hook.Condition.Tool.Equals(toolName, StringComparison.OrdinalIgnoreCase))
                continue;

            await ExecuteHookAsync(hook, vars, ct);
        }
    }

    private readonly record struct HookExecution(int ExitCode, string Stdout, string Stderr);

    private async Task<HookExecution> ExecuteHookAsync(
        HookDefinition hook, Dictionary<string, string> variables, CancellationToken ct)
    {
        var command = hook.Run;
        foreach (var (key, value) in variables)
            command = command.Replace($"{{{{{key}}}}}", value);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                ArgumentList = { "-c", command },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _config.WorkingDirectory,
            };

            var process = Process.Start(psi);
            if (process is null)
            {
                _warn?.Invoke($"Hook failed to start: {command}");
                return new HookExecution(-1, "", "");
            }

            using (process)
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

                var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
                await process.WaitForExitAsync(timeoutCts.Token);
                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                if (process.ExitCode != 0 && process.ExitCode != BlockExitCode)
                {
                    _warn?.Invoke($"Hook exited with code {process.ExitCode}: {command}" +
                        (string.IsNullOrEmpty(stderr) ? "" : $"\n  {stderr.Trim()}"));
                }

                return new HookExecution(process.ExitCode, stdout, stderr);
            }
        }
        catch (OperationCanceledException)
        {
            _warn?.Invoke($"Hook timed out (30s): {command}");
            return new HookExecution(-1, "", "");
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"Hook failed: {command} — {ex.Message}");
            return new HookExecution(-1, "", "");
        }
    }
}
