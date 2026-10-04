using System.Text.RegularExpressions;

namespace OpenMono.HostBridge;

/// <summary>
/// Paper trail for bare-metal execution. Appends to the EXISTING agent log
/// (<c>~/.openmono/logs/openmono-&lt;date&gt;.log</c>, same line format as
/// <c>OpenMono.Utils.Log</c> — the container agent writes the same file
/// through its bind mount), tagged <c>[host-exec]</c> for greppability.
/// Commands and results are redacted with the same spirit as
/// <c>OpenMono.Utils.SecretScanner</c> (Redact policy): the audit trail never
/// carries passwords or secret values, while the live turn stream still shows
/// the operator the raw output they need to troubleshoot.
/// </summary>
public static class AuditLog
{
    public static void WriteCommand(
        string user,
        bool asRoot,
        string command,
        int exitCode,
        long elapsedMs,
        bool allowed,
        string? note = null)
    {
        var line = $"[host-exec] user={user} as_root={asRoot.ToString().ToLowerInvariant()} " +
            $"exit={exitCode} elapsed_ms={elapsedMs} allowed={allowed.ToString().ToLowerInvariant()} " +
            $"cmd=\"{LogRedactor.Redact(command)}\"" +
            (string.IsNullOrWhiteSpace(note) ? "" : $" note=\"{LogRedactor.Redact(note)}\"");
        Append(line);
    }

    public static void WriteNote(string message) => Append("[host-exec] " + LogRedactor.Redact(message));

    private static void Append(string message)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".openmono", "logs", $"openmono-{DateTime.UtcNow:yyyy-MM-dd}.log");
        var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}] [INFO] {message}{Environment.NewLine}";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, line);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(50);
            }
            catch (Exception)
            {
                return;
            }
        }
    }
}

/// <summary>
/// Narrow redactor for the audit trail: key=value / key:value / Bearer-style
/// secrets and --password flags. Deliberately conservative — it only fires on
/// explicit secret shapes, so ordinary ops output stays readable.
/// </summary>
public static class LogRedactor
{
    private static readonly Regex KeyValue = new(
        @"(?i)\b(password|passwd|pwd|secret|api[_-]?key|auth[_-]?token|access[_-]?token|client[_-]?secret)\s*[:=]\s*(""[^""]*""|'[^']*'|\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Bearer = new(
        @"(?i)\b(bearer)\s+[A-Za-z0-9\-._~+/=]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PasswordFlag = new(
        @"(?i)(--password[ =])([^\s""']+|""[^""]*""|'[^']*')",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Redact(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return content ?? "";
        content = KeyValue.Replace(content, "$1=[REDACTED]");
        content = Bearer.Replace(content, "$1 [REDACTED]");
        content = PasswordFlag.Replace(content, "$1[REDACTED]");
        return content;
    }
}
