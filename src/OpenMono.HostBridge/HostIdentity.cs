using System.Diagnostics;
using System.Security.Cryptography;

namespace OpenMono.HostBridge;

/// <summary>
/// Who bare-metal commands run as. The username is ordinary config; the
/// password (when elevation needs one) lives ONLY in this object, in memory,
/// zeroed on dispose. It is never serialized, never logged, and never passed
/// on any command line — sudo receives it over a piped stdin stream.
/// </summary>
public sealed class HostIdentity : IDisposable
{
    private char[]? _password;
    private bool _disposed;

    public string RunAsUser { get; }
    public bool AllowSudo { get; }
    public bool HasPassword => _password is { Length: > 0 };

    public HostIdentity(string runAsUser, bool allowSudo, char[]? password)
    {
        RunAsUser = string.IsNullOrWhiteSpace(runAsUser) ? Environment.UserName : runAsUser;
        AllowSudo = allowSudo;
        _password = password;
    }

    public static string CurrentUser => Environment.UserName;

    /// <summary>
    /// Reads a password from a file without ever echoing it. The file must be
    /// owner-read/write-only; when this process runs as root the file must
    /// additionally be root-owned. Anything else is refused.
    /// </summary>
    public static char[] ReadPasswordFile(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException($"password file not found: {path}");
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            const UnixFileMode leaked =
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((mode & leaked) != 0)
                throw new InvalidOperationException(
                    $"refusing to read password file with loose permissions ({mode}): {path} " +
                    "(owner read/write only, e.g. chmod 0600).");
            if (EffectiveUid() == 0 && OwnerUid(path) is { } owner && owner != 0)
                throw new InvalidOperationException(
                    $"refusing to read password file not owned by root: {path}.");
        }
        using var reader = new StreamReader(path);
        var line = reader.ReadLine();
        if (string.IsNullOrEmpty(line))
            throw new InvalidOperationException($"password file is empty: {path}.");
        return line.ToCharArray();
    }

    /// <summary>
    /// Masked prompt (no echo). The returned chars must be handed to a
    /// <see cref="HostIdentity"/> (or zeroed by the caller).
    /// </summary>
    public static char[] PromptPassword(string prompt, TextWriter err)
    {
        err.Write(prompt);
        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0)
                {
                    chars.RemoveAt(chars.Count - 1);
                    err.Write("\b \b");
                }
                continue;
            }
            chars.Add(key.KeyChar);
            err.Write("*");
        }
        err.WriteLine();
        return chars.ToArray();
    }

    /// <summary>
    /// Runs <paramref name="use"/> with a UTF-8 copy of the password, then
    /// zeroes the copy. Never call ToString on the password.
    /// </summary>
    public T UsePasswordBytes<T>(Func<byte[], T> use)
    {
        if (_password is null)
            throw new InvalidOperationException("no run-as password in memory.");
        var bytes = System.Text.Encoding.UTF8.GetBytes(_password);
        try
        {
            return use(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void SetPassword(char[] password)
    {
        ClearPassword();
        _password = password;
    }

    private void ClearPassword()
    {
        if (_password is not null)
        {
            CryptographicOperations.ZeroMemory(System.Text.Encoding.UTF8.GetBytes(_password));
            Array.Clear(_password);
            _password = null;
        }
    }

    private static uint? OwnerUid(string path)
    {
        foreach (var args in new[] { "-c %u", "-f %u" })
        {
            try
            {
                var psi = new ProcessStartInfo("stat", $"{args} \"{path}\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi);
                if (process is null)
                    continue;
                var output = process.StandardOutput.ReadToEnd().Trim();
                process.WaitForExit(5_000);
                if (process.ExitCode == 0 && uint.TryParse(output, out var uid))
                    return uid;
            }
            catch (Exception)
            {
            }
        }
        return null;
    }

    private static uint EffectiveUid()
    {
        try
        {
            var psi = new ProcessStartInfo("id", "-u")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return 999_999;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(5_000);
            return uint.TryParse(output, out var uid) ? uid : 999_999;
        }
        catch (Exception)
        {
            return 999_999;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ClearPassword();
        GC.SuppressFinalize(this);
    }
}
