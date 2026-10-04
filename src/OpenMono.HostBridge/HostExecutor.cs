using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenMono.HostBridge;

/// <summary>
/// A host command requested through the AskUser ACP channel.
/// Wire format (set by playbooks/operator prompts, parsed here):
/// <c>HOST_EXEC:</c> followed by either a raw shell command or a JSON object
/// <c>{"command": "...", "timeout_ms": 60000, "background": false, "as_root": false}</c>.
/// The JSON may carry literal newlines inside the command string (models emit
/// raw heredocs more often than escaped ones) — a lenient fallback accepts
/// them, guarded so a truncated value can never execute.
/// </summary>
public sealed record HostExecRequest(string Command, int? TimeoutMs, bool Background, bool AsRoot)
{
    public const string Prefix = "HOST_EXEC:";

    public static bool TryParse(string question, out HostExecRequest? request)
    {
        request = null;
        if (question is null)
            return false;
        var rest = question.StartsWith(Prefix, StringComparison.Ordinal)
            ? question[Prefix.Length..].Trim()
            : question.Trim();
        if (rest.Length == 0)
            return false;

        if (rest.StartsWith('{'))
        {
            if (TryParseStrict(rest, out request))
                return true;
            return TryParseLenient(rest, out request);
        }

        request = new HostExecRequest(rest, null, false, false);
        return true;
    }

    /// <summary>
    /// True when this question claims to be a HOST_EXEC envelope, whether or
    /// not it parses. The bridge must never park the operator in front of one
    /// of these: parseable ones execute, the rest get an auto-reply so the
    /// turn resumes instead of stalling on PENDING_RESPONSE forever.
    /// </summary>
    public static bool LooksLikeHostExec(string question) =>
        !string.IsNullOrWhiteSpace(question) &&
        question.TrimStart().StartsWith(Prefix, StringComparison.Ordinal);

    private static bool TryParseStrict(string rest, out HostExecRequest? request)
    {
        request = null;
        try
        {
            using var doc = JsonDocument.Parse(rest);
            var root = doc.RootElement;
            if (!root.TryGetProperty("command", out var cmdEl) ||
                cmdEl.GetString() is not { Length: > 0 } command)
                return false;
            int? timeout = root.TryGetProperty("timeout_ms", out var tEl) &&
                tEl.TryGetInt32(out var t) ? t : null;
            var background = root.TryGetProperty("background", out var bEl) &&
                bEl.ValueKind == JsonValueKind.True;
            var asRoot = root.TryGetProperty("as_root", out var rEl) &&
                rEl.ValueKind == JsonValueKind.True;
            request = new HostExecRequest(command, timeout, background, asRoot);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static readonly Regex LenientTail = new(
        @"^\s*(,\s*""(?<key>timeout_ms|background|as_root)""\s*:\s*(?<val>-?\d+|true|false)\s*)*}\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LenientTimeout = new(
        @"""timeout_ms""\s*:\s*(?<t>-?\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LenientFlag = new(
        @"""(?<key>background|as_root)""\s*:\s*(?<v>true|false)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Accepts what strict JSON rejects: a <c>"command"</c> string containing
    /// literal newlines/tabs (raw heredocs). The value is scanned honoring
    /// backslash escapes, and whatever follows the closing quote must be
    /// exactly the known sibling fields plus <c>}</c> — an unescaped quote
    /// inside the command fails that check, so a truncated value can never
    /// execute. Anything else is rejected for the auto-error path.
    /// </summary>
    private static bool TryParseLenient(string rest, out HostExecRequest? request)
    {
        request = null;
        var keyIdx = rest.IndexOf("\"command\"", StringComparison.Ordinal);
        if (keyIdx < 0)
            return false;
        var i = keyIdx + "\"command\"".Length;
        i = SkipWs(rest, i);
        if (i >= rest.Length || rest[i] != ':')
            return false;
        i = SkipWs(rest, i + 1);
        if (i >= rest.Length || rest[i] != '"')
            return false;

        var command = new System.Text.StringBuilder(rest.Length);
        i++;
        var closed = false;
        while (i < rest.Length)
        {
            var ch = rest[i];
            if (ch == '\\' && i + 1 < rest.Length)
            {
                // Honor escapes like strict JSON; anything unknown keeps both chars.
                var next = rest[i + 1];
                command.Append(next switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'b' => '\b',
                    'f' => '\f',
                    _ => next,
                });
                if (next == 'u' && i + 5 < rest.Length)
                {
                    // Best effort: keep the raw sequence when it is not hex.
                    var hex = rest.Substring(i + 2, 4);
                    if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var cp))
                    {
                        command.Length--;
                        command.Append(char.ConvertFromUtf32(cp));
                        i += 6;
                        continue;
                    }
                }
                i += 2;
                continue;
            }
            if (ch == '"')
            {
                closed = true;
                i++;
                break;
            }
            // Literal newlines/tabs (raw heredoc) are accepted here — this is
            // the whole point of the lenient path. Other C0 controls reject.
            if (ch < 0x20 && ch != '\n' && ch != '\r' && ch != '\t')
                return false;
            command.Append(ch);
            i++;
        }
        if (!closed || command.Length == 0)
            return false;

        var tail = rest[i..];
        var tailMatch = LenientTail.Match(tail);
        if (!tailMatch.Success)
            return false;

        int? timeout = null;
        var timeoutMatch = LenientTimeout.Match(tail);
        if (timeoutMatch.Success && int.TryParse(timeoutMatch.Groups["t"].Value, out var t))
            timeout = t;
        var background = false;
        var asRoot = false;
        foreach (Match flag in LenientFlag.Matches(tail))
        {
            var on = flag.Groups["v"].Value == "true";
            if (flag.Groups["key"].Value == "background")
                background = on;
            else
                asRoot = on;
        }
        request = new HostExecRequest(command.ToString(), timeout, background, asRoot);
        return true;
    }

    private static int SkipWs(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i]))
            i++;
        return i;
    }
}

public enum HostPolicyDecision { Allow, Deny, Ask }

/// <summary>
/// Executes approved commands on bare metal: the bridge's half of the deal.
/// The model loop stays in the container; this is the only piece that touches
/// the host. Elevation goes through sudo with the run-as identity — and only
/// when the operator opted in (<c>allow_sudo</c>). Every run and every denial
/// is appended to the shared agent audit log; passwords never are.
/// </summary>
public sealed class HostExecutor
{
    private const int MaxOutputChars = 50_000;
    private static readonly TimeSpan SudoProbeTtl = TimeSpan.FromMinutes(5);
    private static readonly Dictionary<string, DateTime> SudoProbeCache = [];
    private static readonly object SudoProbeLock = new();
    private static bool? _sudoPresent;

    private static readonly Regex ExplicitSudo =
        new(@"(^|[\s;&|(|{`])sudo(\s|$)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

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
        if (MatchesAny(command, _policy.Deny))
            return HostPolicyDecision.Deny;
        if (MatchesAny(command, _policy.Allow))
            return HostPolicyDecision.Allow;
        return _policy.Default.Trim().ToLowerInvariant() switch
        {
            "allow" => HostPolicyDecision.Allow,
            "deny" => HostPolicyDecision.Deny,
            _ => HostPolicyDecision.Ask,
        };
    }

    public async Task<string> ExecuteAsync(HostExecRequest request, int defaultTimeoutMs, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var command = request.Command;

        // Explicit sudo in the text is only honored when sudo was opted in;
        // otherwise it is a hard denial, never a silent strip or escalation.
        var explicitSudo = ExplicitSudo.IsMatch(command);
        var targetUser = request.AsRoot ? "root" : _identity.RunAsUser;
        var needsSudo = request.AsRoot ||
            explicitSudo ||
            !targetUser.Equals(HostIdentity.CurrentUser, StringComparison.Ordinal);

        if (needsSudo && !_identity.AllowSudo)
        {
            var reason = request.AsRoot
                ? "as_root requested but sudo is not allowed for this bridge (allow_sudo=false)."
                : $"must run as '{targetUser}' but sudo is not allowed for this bridge (allow_sudo=false). " +
                  "Run the bridge as that user, or enable allow_sudo.";
            AuditLog.WriteCommand(targetUser, request.AsRoot, command, -1, stopwatch.ElapsedMilliseconds, false, reason);
            return $"ERROR: {reason}";
        }

        string result;
        if (!needsSudo)
        {
            result = request.Background
                ? RunBackgroundDirect(command)
                : await RunForegroundDirectAsync(command, defaultTimeoutMs, request.TimeoutMs, ct);
        }
        else if (explicitSudo && !request.AsRoot && targetUser.Equals(HostIdentity.CurrentUser, StringComparison.Ordinal))
        {
            // Operator's own sudo, passed through unwrapped (stdin closed: fails closed, never prompts).
            result = request.Background
                ? RunBackgroundDirect(command)
                : await RunForegroundDirectAsync(command, defaultTimeoutMs, request.TimeoutMs, ct);
        }
        else
        {
            result = request.Background
                ? RunBackgroundSudo(command, targetUser, ct)
                : await RunForegroundSudoAsync(command, targetUser, defaultTimeoutMs, request.TimeoutMs, ct);
        }

        var exit = result.StartsWith("ERROR:", StringComparison.Ordinal) ? -1 : ParseExit(result);
        AuditLog.WriteCommand(targetUser, request.AsRoot, command, exit, stopwatch.ElapsedMilliseconds, true);
        return result;
    }

    /// <summary>
    /// True when this request would elevate AND sudo would demand a password we
    /// do not have — the caller can prompt the operator before executing.
    /// </summary>
    public bool NeedsPassword(HostExecRequest request)
    {
        var targetUser = request.AsRoot ? "root" : _identity.RunAsUser;
        var needsSudo = request.AsRoot ||
            ExplicitSudo.IsMatch(request.Command) ||
            !targetUser.Equals(HostIdentity.CurrentUser, StringComparison.Ordinal);
        if (!needsSudo || !_identity.AllowSudo || !SudoPresent() || _identity.HasPassword)
            return false;
        return !SudoProbe(targetUser);
    }

    private async Task<string> RunForegroundDirectAsync(
        string command, int defaultTimeoutMs, int? requestTimeoutMs, CancellationToken ct)
    {
        var timeoutMs = requestTimeoutMs is > 0 ? Math.Min(requestTimeoutMs.Value, 600_000) : defaultTimeoutMs;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

        var psi = BashPsi(command, stdin: false);
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

        string stdout, stderr;
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            stdout = await stdoutTask;
            stderr = await stderrTask;
        }
        catch (OperationCanceledException)
        {
            TryKillTree(process);
            return ct.IsCancellationRequested
                ? $"ERROR: command cancelled: {command}"
                : $"ERROR: command timed out after {timeoutMs}ms and was terminated: {command}";
        }
        return FormatResult(process.ExitCode, stdout, stderr);
    }

    private async Task<string> RunForegroundSudoAsync(
        string command, string targetUser, int defaultTimeoutMs, int? requestTimeoutMs, CancellationToken ct)
    {
        if (!SudoPresent())
            return "ERROR: sudo is not installed on this host.";
        var timeoutMs = requestTimeoutMs is > 0 ? Math.Min(requestTimeoutMs.Value, 600_000) : defaultTimeoutMs;

        // Fast path: cached timestamp / NOPASSWD — no password touches stdin.
        if (SudoProbe(targetUser))
            return await RunSudoAttemptAsync(command, targetUser, usePassword: false, timeoutMs, ct);

        if (!_identity.HasPassword)
            return $"ERROR: elevation to '{targetUser}' needs a sudo password and none is in memory. " +
                "Restart the bridge interactively to provide it, or set OPENMONO_HOST_PASSWORD_FILE.";

        var result = await RunSudoAttemptAsync(command, targetUser, usePassword: true, timeoutMs, ct);
        if (IsPasswordFailure(result))
        {
            // Timestamp may have expired between probe and run — one retry.
            ForgetSudoProbe(targetUser);
            result = await RunSudoAttemptAsync(command, targetUser, usePassword: true, timeoutMs, ct);
        }
        return result;
    }

    private async Task<string> RunSudoAttemptAsync(
        string command, string targetUser, bool usePassword, int timeoutMs, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs);

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

        try
        {
            if (usePassword)
            {
                _identity.UsePasswordBytes(bytes =>
                {
                    process.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                    process.StandardInput.BaseStream.WriteByte((byte)'\n');
                    process.StandardInput.BaseStream.Flush();
                    return 0;
                });
            }
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            return FormatResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(process);
            return $"ERROR: command timed out after {timeoutMs}ms and was terminated: {command}";
        }
    }

    private string RunBackgroundDirect(string command)
    {
        Directory.CreateDirectory(_logDir);
        var logPath = NewBgLogPath();
        var wrapped = $"exec >>'{logPath}' 2>&1; {command}";
        return SpawnDetached(wrapped, stdinPassword: false, command, logPath);
    }

    private string RunBackgroundSudo(string command, string targetUser, CancellationToken ct)
    {
        if (!SudoPresent())
            return "ERROR: sudo is not installed on this host.";
        Directory.CreateDirectory(_logDir);
        var logPath = NewBgLogPath();
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

    private string NewBgLogPath() => Path.Combine(_logDir,
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

    private static bool IsPasswordFailure(string result) =>
        result.Contains("a password is required", StringComparison.OrdinalIgnoreCase) ||
        result.Contains("no tty present", StringComparison.OrdinalIgnoreCase) ||
        result.Contains("incorrect password attempts", StringComparison.OrdinalIgnoreCase);

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

    private static bool SudoProbeOnce(string targetUser)
    {
        if (!SudoPresent())
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

    private static void TryKillTree(Process process)
    {
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

    internal static bool GlobMatch(string pattern, string value)
    {
        var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(value, regex,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
