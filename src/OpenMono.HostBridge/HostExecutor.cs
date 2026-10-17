using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenMono.HostBridge;

public enum HostPolicyDecision { Allow, Deny, Ask }

/// <summary>
/// Executes approved commands on bare metal: the bridge's half of the deal.
/// The model loop stays in the container; this is the only piece that touches
/// the host. Elevation goes through sudo with the run-as identity — and only
/// when the operator opted in (<c>allow_sudo=allow</c>). Every run and every denial
/// is appended to the shared agent audit log; passwords never are.
/// </summary>
public sealed class HostExecutor
{
    private const int MaxOutputChars = 50_000;
    private static readonly TimeSpan SudoProbeTtl = TimeSpan.FromMinutes(5);
    private static readonly Dictionary<string, DateTime> SudoProbeCache = [];
    private static readonly object SudoProbeLock = new();
    private static bool? _sudoPresent;

    private static readonly Regex ExplicitSudoLegacy =
        new(@"(^|[\s;&|(|{`])sudo(\s|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] EscalationPrograms =
        ["sudo", "su", "doas", "pkexec", "run0"];

    private readonly HostExecPolicy _policy;
    private readonly string _workDir;
    private readonly string _logDir;
    private readonly HostIdentity _identity;

    public HostIdentity Identity => _identity;

    public HostExecutor(HostExecPolicy policy, string workDir, string logDir, HostIdentity identity)
    {
        _policy = policy;
        _workDir = workDir;
        _logDir = logDir;
        _identity = identity;
    }

    public HostPolicyDecision CheckPolicy(string command)
    {
        if (MatchesDeny(command, _policy.Deny))
            return HostPolicyDecision.Deny;
        var def = _policy.Default.Trim().ToLowerInvariant();
        // Ask-by-default: every command prompts. The allow list must never
        // skip the prompt in ask mode; it only auto-allows when the default
        // is allow (and then only for single simple commands, see below).
        if (def == "ask")
            return HostPolicyDecision.Ask;
        if (MatchesAllow(command, _policy.Allow))
            return HostPolicyDecision.Allow;
        return def switch
        {
            "allow" => HostPolicyDecision.Allow,
            "deny" => HostPolicyDecision.Deny,
            _ => HostPolicyDecision.Ask,
        };
    }

    public async Task<string> ExecuteAsync(HostExecRequest request, int defaultTimeoutMs, CancellationToken ct, IHostExecPrompter? prompter = null)
    {
        var stopwatch = Stopwatch.StartNew();
        var command = request.Command;

        // Explicit privilege escalation in the text is only honored when sudo
        // was opted in; otherwise it is a hard denial, never a silent strip
        // or escalation. Detection is per command segment by program basename
        // so /usr/bin/sudo, \sudo, quoted forms, su, doas, pkexec, run0 are
        // all caught.
        var explicitEscalation = ContainsEscalation(command);
        var targetUser = request.AsRoot ? "root" : _identity.RunAsUser;
        if (!HostIdentity.IsValidUsername(targetUser))
        {
            var badUser = $"refusing to run as invalid username '{targetUser}'.";
            AuditLog.WriteCommand(targetUser, request.AsRoot, command, -1, stopwatch.ElapsedMilliseconds, false, badUser);
            return $"ERROR: {badUser}";
        }
        var needsSudo = RequiresElevation(request);

        if (needsSudo && _identity.Sudo == SudoPolicy.Deny)
        {
            // Deny = hard refusal. Ask = already approved per-command by the
            // bridge (the operator said yes to exactly this command, in memory
            // only); Allow = policy. Anything else reaching this line is a bug.
            var reason = request.AsRoot
                ? "as_root requested but sudo is denied for this bridge (allow_sudo=deny)."
                : explicitEscalation
                    ? $"privilege escalation detected but sudo is denied for this bridge (allow_sudo=deny). " +
                      "Remove sudo/su/doas/pkexec/run0 from the command, or set allow_sudo=allow (openmono config host.sudo_policy allow)."
                    : $"must run as '{targetUser}' but sudo is denied for this bridge (allow_sudo=deny). " +
                      "Run the bridge as that user, or set allow_sudo=allow (openmono config host.sudo_policy allow).";
            AuditLog.WriteCommand(targetUser, request.AsRoot, command, -1, stopwatch.ElapsedMilliseconds, false, reason);
            return $"ERROR: {reason}";
        }

        string result;
        if (!needsSudo)
        {
            result = request.Background
                ? RunBackgroundDirect(command)
                : await RunForegroundDirectAsync(command, defaultTimeoutMs, request.TimeoutMs, prompter, ct);
        }
        else if (explicitEscalation && !request.AsRoot && targetUser.Equals(HostIdentity.CurrentUser, StringComparison.Ordinal))
        {
            // Operator's own sudo, passed through unwrapped. The prompt loop
            // still applies (fails fast with guidance when no prompter).
            result = request.Background
                ? RunBackgroundDirect(command)
                : await RunForegroundDirectAsync(command, defaultTimeoutMs, request.TimeoutMs, prompter, ct);
        }
        else
        {
            result = request.Background
                ? RunBackgroundSudo(command, targetUser, ct)
                : await RunForegroundSudoAsync(command, targetUser, defaultTimeoutMs, request.TimeoutMs, prompter, ct);
        }

        var exit = result.StartsWith("ERROR:", StringComparison.Ordinal) ? -1 : ParseExit(result);
        AuditLog.WriteCommand(targetUser, request.AsRoot, command, exit, stopwatch.ElapsedMilliseconds, true);
        return result;
    }

    /// <summary>
    /// True when this request would run with privilege escalation (as_root,
    /// an escalation keyword, or a run-as user that is not the current user).
    /// </summary>
    public bool RequiresElevation(HostExecRequest request)
    {
        var targetUser = request.AsRoot ? "root" : _identity.RunAsUser;
        if (!HostIdentity.IsValidUsername(targetUser))
            return false;
        return request.AsRoot ||
            ContainsEscalation(request.Command) ||
            !targetUser.Equals(HostIdentity.CurrentUser, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when this request would elevate AND sudo would demand a password we
    /// do not have — the caller can prompt the operator before executing.
    /// </summary>
    public bool NeedsPassword(HostExecRequest request)
    {
        var targetUser = request.AsRoot ? "root" : _identity.RunAsUser;
        if (!HostIdentity.IsValidUsername(targetUser))
            return false;
        if (!RequiresElevation(request) || _identity.Sudo == SudoPolicy.Deny || !SudoPresent() || _identity.HasPassword)
            return false;
        return !SudoProbe(targetUser);
    }

    private async Task<string> RunForegroundDirectAsync(
        string command, int defaultTimeoutMs, int? requestTimeoutMs, IHostExecPrompter? prompter, CancellationToken ct)
    {
        var timeoutMs = requestTimeoutMs is > 0 ? Math.Min(requestTimeoutMs.Value, 600_000) : defaultTimeoutMs;

        // stdin stays open: when the command blocks on input, the prompt loop
        // asks the operator (or fails fast with guidance when no prompter).
        var psi = BashPsi(command, stdin: true);
        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                return "ERROR: failed to start process.";
        }
        catch (Exception ex)
        {
            return $"ERROR: failed to start process: {ex.Message}";
        }

        var read = await InteractiveProcessRunner.ReadAsync(
            process, prompter, timeoutMs, ct, p => TryKillTree(p, useSudo: false));
        if (read.Cancelled)
            return $"ERROR: command cancelled: {command}";
        if (read.AbortError is not null)
            return read.AbortError;
        if (read.TimedOut)
            return TimeoutMessage(timeoutMs, command, SafePid(process), confirmedTerminated: true);
        return FormatResult(process.ExitCode, read.Stdout, read.Stderr);
    }

    private async Task<string> RunForegroundSudoAsync(
        string command, string targetUser, int defaultTimeoutMs, int? requestTimeoutMs, IHostExecPrompter? prompter, CancellationToken ct)
    {
        if (!SudoPresent())
            return "ERROR: sudo is not installed on this host.";
        if (!HostIdentity.IsValidUsername(targetUser))
            return $"ERROR: refusing to run as invalid username '{targetUser}'.";
        var timeoutMs = requestTimeoutMs is > 0 ? Math.Min(requestTimeoutMs.Value, 600_000) : defaultTimeoutMs;

        // Fast path: cached timestamp / NOPASSWD — no password touches stdin.
        if (SudoProbe(targetUser))
            return await RunSudoAttemptAsync(command, targetUser, usePassword: false, timeoutMs, prompter, ct);

        if (!_identity.HasPassword)
            return $"ERROR: elevation to '{targetUser}' needs a sudo password and none is in memory. " +
                "Restart the bridge interactively to provide it, or set OPENMONO_HOST_PASSWORD_FILE.";

        var result = await RunSudoAttemptAsync(command, targetUser, usePassword: true, timeoutMs, prompter, ct);
        // No same-password retry here: a password failure means the in-memory
        // secret is wrong (or sudo policy changed). The caller (Bridge) owns
        // the retry loop: wipe + re-prompt. Retrying with identical bytes
        // only burns sudo's limited attempts and risks lockout.
        return result;
    }

    private async Task<string> RunSudoAttemptAsync(
        string command, string targetUser, bool usePassword, int timeoutMs, IHostExecPrompter? prompter, CancellationToken ct)
    {
        if (!HostIdentity.IsValidUsername(targetUser))
            return $"ERROR: refusing to run as invalid username '{targetUser}'.";
        var inner = $"sudo {(usePassword ? "-S " : "")}-u {targetUser} -- /bin/bash -c {Escape(command)}";
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            ArgumentList = { "-c", inner },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workDir,
        };
        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
                return "ERROR: failed to start sudo.";
        }
        catch (Exception ex)
        {
            return $"ERROR: failed to start sudo: {ex.Message}";
        }

        // Sudo password first (when needed), then the prompt loop for any
        // further input the command itself requests. Stdin stays open.
        if (usePassword)
        {
            try
            {
                _identity.UsePasswordBytes(bytes =>
                {
                    process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                    process.StandardInput.BaseStream.WriteByte((byte)'\n');
                    process.StandardInput.BaseStream.Flush();
                    return 0;
                });
            }
            catch (IOException)
            {
                return "ERROR: failed to provide sudo password (process exited early).";
            }
        }

        var read = await InteractiveProcessRunner.ReadAsync(
            process, prompter, timeoutMs, ct, p => TryKillTree(p, useSudo: true));
        if (read.Cancelled)
            return $"ERROR: command cancelled: {command}";
        if (read.AbortError is not null)
            return read.AbortError;
        if (read.TimedOut)
            return TimeoutMessage(timeoutMs, command, SafePid(process), confirmedTerminated: true);
        return FormatResult(process.ExitCode, read.Stdout, read.Stderr);
    }

    private string RunBackgroundDirect(string command)
    {
        var dir = ResolveBgLogDir();
        if (dir is null)
            return "ERROR: no writable log directory for background commands (host-bridge/logs, ~/.openmono/logs and temp are all unwritable).";
        var logPath = NewBgLogPath(dir);
        var wrapped = $"exec >>'{logPath}' 2>&1; {command}";
        return SpawnDetached(wrapped, stdinPassword: false, command, logPath);
    }

    private string RunBackgroundSudo(string command, string targetUser, CancellationToken ct)
    {
        if (!SudoPresent())
            return "ERROR: sudo is not installed on this host.";
        var dir = ResolveBgLogDir();
        if (dir is null)
            return "ERROR: no writable log directory for background commands (host-bridge/logs, ~/.openmono/logs and temp are all unwritable).";
        var logPath = NewBgLogPath(dir);
        var needsPassword = !SudoProbe(targetUser);
        if (needsPassword && !_identity.HasPassword)
            return $"ERROR: elevation to '{targetUser}' needs a sudo password and none is in memory.";
        var wrapped = $"exec >>'{logPath}' 2>&1; sudo {(needsPassword ? "-S " : "")}-u {targetUser} -- /bin/bash -c {Escape(command)}";
        return SpawnDetached(wrapped, needsPassword, command, logPath);
    }

    private string SpawnDetached(string wrapped, bool stdinPassword, string command, string logPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            ArgumentList = { "-c", wrapped },
            RedirectStandardInput = stdinPassword,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workDir,
        };
        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return "ERROR: failed to start background process.";
            if (stdinPassword)
            {
                _identity.UsePasswordBytes(bytes =>
                {
                    process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                    process.StandardInput.BaseStream.WriteByte((byte)'\n');
                    process.StandardInput.BaseStream.Flush();
                    return 0;
                });
                process.StandardInput.Close();
            }
            return $"Started in background — PID {process.Id} as {EffectiveUserHint()}\nLog: {logPath}\n" +
                $"Poll with another HOST_EXEC call: tail -n 50 {logPath}";
        }
        catch (Exception ex)
        {
            return $"ERROR: failed to start background process: {ex.Message}";
        }
    }

    private string EffectiveUserHint() => _identity.RunAsUser;

    private string? _bgLogDir;

    /// <summary>
    /// The directory used for background-command logs. The configured log dir
    /// can exist but be unwritable — Docker auto-creates the tmpfs shadow's
    /// mountpoint (~/.openmono/host-bridge/logs) as root inside the host bind,
    /// while the daemon runs as the unprivileged user. Walk a fallback chain
    /// (configured dir → ~/.openmono/logs → temp), probing each for write
    /// access, so background exec never dies on a directory permission the
    /// operator cannot see. A one-time audit note names the fallback.
    /// </summary>
    private string? ResolveBgLogDir()
    {
        if (_bgLogDir is not null) return _bgLogDir;
        string[] candidates =
        [
            _logDir,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".openmono", "logs"),
            Path.GetTempPath(),
        ];
        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, $".probe-{Guid.NewGuid():N}.tmp");
                File.AppendAllText(probe, "");
                File.Delete(probe);
                _bgLogDir = dir;
                if (!string.Equals(dir, _logDir, StringComparison.Ordinal))
                    AuditLog.WriteNote($"bg-log dir fallback: {_logDir} is not writable (root-owned? created by Docker); using {dir}");
                return _bgLogDir;
            }
            catch (Exception)
            {
                // Unwritable candidate — try the next one.
            }
        }
        return null;
    }

    private string NewBgLogPath(string dir) => Path.Combine(dir,
        $"bg-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.log");

    private ProcessStartInfo BashPsi(string command, bool stdin)
    {
        return new ProcessStartInfo
        {
            FileName = "/bin/bash",
            ArgumentList = { "-c", command },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workDir,
        };
    }

    private static string Escape(string command) => "'" + command.Replace("'", "'\\''") + "'";

    private static string FormatResult(int exitCode, string stdout, string stderr)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(stdout))
            parts.Add(stdout.TrimEnd());
        if (!string.IsNullOrWhiteSpace(stderr))
            parts.Add("[stderr]\n" + stderr.TrimEnd());
        var content = parts.Count > 0 ? string.Join('\n', parts) : "(no output)";
        if (exitCode != 0)
            content = $"Exit code: {exitCode}\n{content}";
        if (content.Length > MaxOutputChars)
            content = content[..MaxOutputChars] + $"\n... (truncated, {content.Length} total chars)";
        return content;
    }

    private static int ParseExit(string result)
    {
        const string marker = "Exit code: ";
        if (!result.StartsWith(marker, StringComparison.Ordinal))
            return 0;
        var digits = new string(result[marker.Length..].TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var code) ? code : 1;
    }

    /// <summary>
    /// True when sudo rejected the authentication: wrong password, missing
    /// password, or policy refusal. The caller should wipe the in-memory
    /// secret and re-prompt rather than retry with identical bytes.
    /// </summary>
    internal static bool IsPasswordFailure(string result) =>
        result.Contains("a password is required", StringComparison.OrdinalIgnoreCase) ||
        result.Contains("no tty present", StringComparison.OrdinalIgnoreCase) ||
        result.Contains("incorrect password", StringComparison.OrdinalIgnoreCase) ||
        result.Contains("sorry, try again", StringComparison.OrdinalIgnoreCase) ||
        result.Contains("authentication failure", StringComparison.OrdinalIgnoreCase) ||
        result.Contains("sudo: a password is required", StringComparison.OrdinalIgnoreCase);

    private static bool SudoPresent()
    {
        if (_sudoPresent.HasValue)
            return _sudoPresent.Value;
        try
        {
            var psi = new ProcessStartInfo("/bin/bash", "-c \"command -v sudo\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                _sudoPresent = false;
                return false;
            }
            process.WaitForExit(10_000);
            _sudoPresent = process.ExitCode == 0;
            return _sudoPresent.Value;
        }
        catch (Exception)
        {
            _sudoPresent = false;
            return false;
        }
    }

    private static bool SudoProbe(string targetUser)
    {
        lock (SudoProbeLock)
        {
            if (SudoProbeCache.TryGetValue(targetUser, out var until) && DateTime.UtcNow < until)
                return true;
        }
        var ok = SudoProbeOnce(targetUser);
        lock (SudoProbeLock)
        {
            if (ok)
                SudoProbeCache[targetUser] = DateTime.UtcNow + SudoProbeTtl;
            else
                SudoProbeCache.Remove(targetUser);
        }
        return ok;
    }

    private static void ForgetSudoProbe(string targetUser)
    {
        lock (SudoProbeLock)
        {
            SudoProbeCache.Remove(targetUser);
        }
    }

    /// <summary>
    /// Drops a cached passwordless-probe entry after an authentication
    /// failure so the next <c>NeedsPassword</c> check re-probes instead of
    /// trusting a stale "no password needed" result.
    /// </summary>
    internal static void NoteAuthenticationFailure(string targetUser) => ForgetSudoProbe(targetUser);

    private static bool SudoProbeOnce(string targetUser)
    {
        if (!SudoPresent())
            return false;
        if (!HostIdentity.IsValidUsername(targetUser))
            return false;
        try
        {
            var psi = new ProcessStartInfo(
                "/bin/bash", $"-c \"sudo -n -u {targetUser} -- true\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return false;
            try { process.StandardInput.Close(); } catch { }
            process.WaitForExit(15_000);
            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static int SafePid(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    internal static string TimeoutMessage(int timeoutMs, string command, int pid, bool confirmedTerminated)
    {
        if (confirmedTerminated)
            return $"ERROR: command timed out after {timeoutMs}ms and was terminated: {command}";
        var pidHint = pid > 0 ? $" (pid {pid})" : "";
        return $"ERROR: command timed out after {timeoutMs}ms but the process may still be running{pidHint}: {command}";
    }

    /// <summary>
    /// Best-effort kill of a timed-out process tree. Returns true only when
    /// the process is confirmed gone. When the command ran via sudo the
    /// children may be root-owned, so the bridge user's kill silently fails;
    /// in that case escalate the kill itself with passwordless sudo (TERM
    /// then KILL on the pid and its process group) and verify afterwards.
    /// Never claim terminated without confirming.
    /// </summary>
    internal static bool TryKillTree(Process process, bool useSudo)
    {
        try
        {
            if (process.HasExited)
                return true;
        }
        catch (Exception)
        {
            return false;
        }
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (Exception)
        {
        }
        if (WaitForExitBrief(process, 5000))
            return true;
        if (useSudo)
        {
            var pid = SafePid(process);
            if (pid > 0)
                SudoKillPid(pid);
            if (WaitForExitBrief(process, 5000))
                return true;
        }
        try
        {
            return process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool WaitForExitBrief(Process process, int milliseconds)
    {
        try
        {
            return process.HasExited || process.WaitForExit(milliseconds);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void SudoKillPid(int pid)
    {
        // TERM first (pid and process group), then KILL. Passwordless only
        // (-n): never prompt, fail closed when no timestamp/NOPASSWD.
        // Negative-PID group kill verified on macOS (Darwin) as well as
        // Linux; pkill -P is supported by both procps and Darwin pkill.
        foreach (var signal in new[] { "-TERM", "-KILL" })
        {
            RunSudoKillOnce(signal, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            RunSudoKillOnce(signal, "-" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        // Child processes that reparented: best effort via pkill on parent.
        RunSudoPkillOnce("-TERM", pid);
        RunSudoPkillOnce("-KILL", pid);
    }

    private static void RunSudoKillOnce(string signal, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("sudo",
                $"-n kill {signal} {target}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return;
            process.WaitForExit(10_000);
        }
        catch (Exception)
        {
        }
    }

    private static void RunSudoPkillOnce(string signal, int ppid)
    {
        try
        {
            var psi = new ProcessStartInfo("sudo",
                $"-n pkill {signal} -P {ppid.ToString(System.Globalization.CultureInfo.InvariantCulture)}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return;
            process.WaitForExit(10_000);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// True when the command is a single simple command with no shell
    /// metacharacters. Allow-list patterns may only match these; anything
    /// compound (pipes, chains, substitutions, redirections, newlines) is
    /// treated as not matched so it falls to the default (usually ask).
    /// </summary>
    internal static bool IsSingleSimpleCommand(string command)
    {
        if (string.IsNullOrEmpty(command))
            return false;
        if (command.Contains(';') || command.Contains('&') || command.Contains('|') ||
            command.Contains('`') || command.Contains('>') || command.Contains('<') ||
            command.Contains('\n') || command.Contains('\r'))
            return false;
        if (command.Contains("$(", StringComparison.Ordinal) ||
            command.Contains("${", StringComparison.Ordinal))
            return false;
        return true;
    }

    internal static bool MatchesAllow(string command, List<string> patterns)
    {
        if (!IsSingleSimpleCommand(command))
            return false;
        var trimmed = command.Trim();
        foreach (var pattern in patterns)
        {
            if (GlobMatch(pattern, trimmed))
                return true;
        }
        return false;
    }

    internal static bool MatchesDeny(string command, List<string> patterns)
    {
        foreach (var segment in SplitSegments(command))
        {
            var candidates = DenyCandidates(segment);
            foreach (var pattern in patterns)
            {
                foreach (var candidate in candidates)
                {
                    if (GlobMatch(pattern, candidate))
                        return true;
                }
            }
            if (IsDangerousRmSegment(segment))
                return true;
        }
        return false;
    }

    internal static bool MatchesAny(string command, List<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (GlobMatch(pattern, command))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Split a command line into segments on shell chaining/piping operators
    /// and newlines so deny patterns are checked per segment, not just
    /// against the whole line (where "git status; rm -rf ~" would otherwise
    /// match "git *").
    /// </summary>
    internal static List<string> SplitSegments(string command)
    {
        var parts = Regex.Split(command ?? "", @"[;&|\n\r`]+");
        var result = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0)
                result.Add(trimmed);
        }
        if (result.Count == 0 && !string.IsNullOrWhiteSpace(command))
            result.Add(command.Trim());
        return result;
    }

    private static List<string> DenyCandidates(string segment)
    {
        var result = new List<string> { segment };
        var normalized = NormalizeFirstProgram(segment);
        if (!normalized.Equals(segment, StringComparison.Ordinal))
            result.Add(normalized);
        var stripped = StripEscalationPrefix(segment);
        if (stripped is not null && stripped.Length > 0 && !stripped.Equals(segment, StringComparison.Ordinal))
        {
            result.Add(stripped);
            var normalizedStripped = NormalizeFirstProgram(stripped);
            if (!normalizedStripped.Equals(stripped, StringComparison.Ordinal))
                result.Add(normalizedStripped);
        }
        return result;
    }

    /// <summary>
    /// Normalize the program word of one segment to its basename: strips
    /// quoting, a leading backslash (alias bypass), and any directory prefix
    /// so /bin/rm, /usr/bin/rm, and \rm all match a "rm ..." deny pattern.
    /// </summary>
    internal static string NormalizeFirstProgram(string segment)
    {
        var trimmed = segment.TrimStart();
        if (trimmed.Length == 0)
            return segment;
        var token = FirstToken(trimmed);
        if (token.Length == 0)
            return segment;
        var basename = ProgramBasename(token);
        if (basename.Length == 0 || basename.Equals(token, StringComparison.Ordinal))
            return trimmed;
        return basename + trimmed[token.Length..];
    }

    internal static string ProgramBasename(string token)
    {
        var t = token.Trim();
        // Strip surrounding quotes repeatedly ('rm', "rm").
        while (t.Length >= 2 && ((t[0] == '\'' && t[^1] == '\'') || (t[0] == '"' && t[^1] == '"')))
            t = t[1..^1];
        // Leading backslash bypasses aliases: \rm -> rm.
        while (t.StartsWith("\\", StringComparison.Ordinal) && t.Length > 1)
            t = t[1..];
        // Strip quotes again after backslash removal.
        while (t.Length >= 2 && ((t[0] == '\'' && t[^1] == '\'') || (t[0] == '"' && t[^1] == '"')))
            t = t[1..^1];
        // Strip leading ./ or directory prefix: /bin/rm -> rm.
        var slash = t.LastIndexOf('/');
        if (slash >= 0)
            t = t[(slash + 1)..];
        t = t.Trim('\'', '"', '\\');
        return t;
    }

    private static string FirstToken(string segment)
    {
        var i = 0;
        if (segment.StartsWith("'", StringComparison.Ordinal) || segment.StartsWith("\"", StringComparison.Ordinal))
        {
            var quote = segment[0];
            i = 1;
            while (i < segment.Length && segment[i] != quote)
                i++;
            if (i < segment.Length)
                i++;
            return segment[..i];
        }
        while (i < segment.Length && !char.IsWhiteSpace(segment[i]))
            i++;
        return segment[..i];
    }

    private static string? StripEscalationPrefix(string segment)
    {
        var trimmed = segment.TrimStart();
        var token = FirstToken(trimmed);
        if (ProgramBasename(token).Equals("sudo", StringComparison.OrdinalIgnoreCase) ||
            ProgramBasename(token).Equals("su", StringComparison.OrdinalIgnoreCase) ||
            ProgramBasename(token).Equals("doas", StringComparison.OrdinalIgnoreCase) ||
            ProgramBasename(token).Equals("pkexec", StringComparison.OrdinalIgnoreCase) ||
            ProgramBasename(token).Equals("run0", StringComparison.OrdinalIgnoreCase))
        {
            var rest = trimmed[token.Length..].TrimStart();
            // Skip sudo-style flags (-u user, -n, -- ...) so "sudo -u root rm"
            // still exposes the rm for deny matching.
            while (rest.StartsWith("-", StringComparison.Ordinal) && rest.Length > 1)
            {
                var flag = FirstToken(rest);
                rest = rest[flag.Length..].TrimStart();
                // -u/-U take a value; skip it too.
                if (flag is "-u" or "-U" or "--user")
                {
                    var value = FirstToken(rest);
                    rest = rest[value.Length..].TrimStart();
                }
                if (flag == "--")
                    break;
            }
            return rest;
        }
        return null;
    }

    /// <summary>
    /// Catches rm with recursive+force variants targeting / or ~, regardless
    /// of whether the configured deny patterns name that exact flag spelling
    /// (rm -rf, -fr, -r -f, -R -f, --recursive --force, ...).
    /// </summary>
    internal static bool IsDangerousRmSegment(string segment)
    {
        var tokens = Tokenize(segment);
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!ProgramBasename(tokens[i]).Equals("rm", StringComparison.OrdinalIgnoreCase))
                continue;
            var hasRecursive = false;
            var hasForce = false;
            var hasRootTarget = false;
            for (var j = i + 1; j < tokens.Count; j++)
            {
                var arg = tokens[j].Trim('\'', '"');
                if (arg.Length == 0)
                    continue;
                if (arg == "--")
                    continue;
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    if (arg.Equals("--recursive", StringComparison.OrdinalIgnoreCase))
                        hasRecursive = true;
                    else if (arg.Equals("--force", StringComparison.OrdinalIgnoreCase))
                        hasForce = true;
                    else if (arg.StartsWith("--", StringComparison.Ordinal))
                        continue;
                    continue;
                }
                if (arg.StartsWith("-", StringComparison.Ordinal) && arg.Length > 1 && arg != "-")
                {
                    var flags = arg[1..];
                    // Long single-dash? Only single-letter flags matter here.
                    foreach (var c in flags)
                    {
                        if (c is 'r' or 'R')
                            hasRecursive = true;
                        else if (c is 'f')
                            hasForce = true;
                    }
                    continue;
                }
                var target = arg.Trim();
                if (target.Equals("/", StringComparison.Ordinal) ||
                    target.Equals("~", StringComparison.Ordinal) ||
                    target.StartsWith("/*", StringComparison.Ordinal) ||
                    target.StartsWith("~/", StringComparison.Ordinal) ||
                    target.StartsWith("// ", StringComparison.Ordinal))
                {
                    hasRootTarget = true;
                }
                else if (target is "/" or "~")
                {
                    hasRootTarget = true;
                }
            }
            if (hasRecursive && hasForce && hasRootTarget)
                return true;
        }
        return false;
    }

    private static List<string> Tokenize(string segment)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (quote is not null)
            {
                current.Append(c);
                if (c == quote)
                    quote = null;
            }
            else if (c == '\'' || c == '"')
            {
                quote = c;
                current.Append(c);
            }
            else if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }
        if (current.Length > 0)
            tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>
    /// True when any command segment escalates privilege: the program basename
    /// (after stripping paths, backslashes, and quotes) is sudo, su, doas,
    /// pkexec, or run0. Catches /usr/bin/sudo, \sudo, "sudo", and friends in
    /// every ; &amp; | chained segment.
    /// </summary>
    internal static bool ContainsEscalation(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;
        if (ExplicitSudoLegacy.IsMatch(command))
            return true;
        foreach (var segment in SplitSegments(command))
        {
            var trimmed = segment.TrimStart();
            if (trimmed.Length == 0)
                continue;
            // Skip leading env assignments (VAR=x sudo ...) and wrappers like
            // "env sudo": scan the first few tokens for an escalation program.
            var tokens = Tokenize(trimmed);
            var scanned = 0;
            foreach (var token in tokens)
            {
                if (scanned > 3)
                    break;
                if (token.Contains('=', StringComparison.Ordinal) && !token.StartsWith("-", StringComparison.Ordinal))
                {
                    scanned++;
                    continue;
                }
                if (token.Equals("env", StringComparison.OrdinalIgnoreCase))
                {
                    scanned++;
                    continue;
                }
                var baseName = ProgramBasename(token);
                foreach (var prog in EscalationPrograms)
                {
                    if (baseName.Equals(prog, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                break;
            }
            // Parenthesized/subshell-prefixed segments: "(sudo ...", "{ sudo".
            var stripped = trimmed.TrimStart('(', '{');
            if (!stripped.Equals(trimmed, StringComparison.Ordinal))
            {
                var first = FirstToken(stripped.TrimStart());
                var baseName = ProgramBasename(first);
                foreach (var prog in EscalationPrograms)
                {
                    if (baseName.Equals(prog, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        return false;
    }

    internal static bool GlobMatch(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(value, regex,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
