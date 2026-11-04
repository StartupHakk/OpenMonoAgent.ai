// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;

namespace Omabot.Common;

/// <summary>
/// File helpers with fixed modes. Writes are atomic: a temp file in the same
/// folder, flushed, then renamed over the target, so a crash never leaves a
/// torn file (plan 7.3 "File safety").
/// </summary>
public static class SecureFile
{
    public const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    public const UnixFileMode PrivateDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    public const UnixFileMode SharedReadDir = PrivateDir | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>Test hook: when set, called after the temp file is written and before the rename.</summary>
    internal static Action<string>? BeforeRenameForTests { get; set; }

    public static bool IsUnix => !OperatingSystem.IsWindows();

    public static void EnsureDir(string path, UnixFileMode mode = PrivateDir)
    {
        Directory.CreateDirectory(path);
        if (IsUnix) File.SetUnixFileMode(path, mode);
    }

    /// <summary>Create a folder only when it is missing. Never touches files inside (lesson L1).</summary>
    public static void EnsureDirNoClobber(string path, UnixFileMode mode = PrivateDir)
    {
        if (Directory.Exists(path)) return;
        EnsureDir(path, mode);
    }

    public static void WriteAllTextAtomic(string path, string content, UnixFileMode mode = PrivateFile)
        => WriteAllBytesAtomic(path, Utf8NoBom.GetBytes(content), mode);

    public static void WriteAllBytesAtomic(string path, byte[] content, UnixFileMode mode = PrivateFile)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new IOException("No folder for " + path);
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = IsUnix ? mode : null,
            }))
            {
                fs.Write(content);
                fs.Flush(true);
            }
            if (IsUnix) File.SetUnixFileMode(tmp, mode);
            BeforeRenameForTests?.Invoke(tmp);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    /// <summary>Write only when the file does not exist yet. Returns true when written.</summary>
    public static bool WriteIfMissing(string path, string content, UnixFileMode mode = PrivateFile)
    {
        if (File.Exists(path)) return false;
        WriteAllTextAtomic(path, content, mode);
        return true;
    }

    public static void TrySetMode(string path, UnixFileMode mode = PrivateFile)
    {
        if (IsUnix && File.Exists(path)) File.SetUnixFileMode(path, mode);
    }

    public static string ReadText(string path) => File.ReadAllText(path, Encoding.UTF8);

    public static UnixFileMode? ModeOf(string path)
    {
        if (!IsUnix) return null;
        if (!File.Exists(path) && !Directory.Exists(path)) return null;
        return File.GetUnixFileMode(path);
    }

    /// <summary>True when group and other have no access at all.</summary>
    public static bool IsPrivate(string path)
    {
        var mode = ModeOf(path);
        if (mode is null) return !IsUnix;
        const UnixFileMode groupOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        return (mode.Value & groupOther) == 0;
    }

    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string HashOfFile(string path) => File.Exists(path) ? Sha256Hex(File.ReadAllBytes(path)) : "absent";

    /// <summary>Constant time compare for tokens.</summary>
    public static bool FixedTimeEquals(string? a, string? b)
    {
        if (a is null || b is null) return false;
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ab, bb);
    }

    public static string NewToken(int bytes = 32)
        => "omb_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>True when any segment of the path is a symlink (checked from the root down).</summary>
    public static bool HasSymlinkSegment(string fullPath)
    {
        var current = Path.GetPathRoot(fullPath) ?? "/";
        foreach (var part in fullPath[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (info.Exists && info.LinkTarget is not null) return true;
            var finfo = new FileInfo(current);
            if (finfo.Exists && finfo.LinkTarget is not null) return true;
        }
        return false;
    }

    public static string NormalizeLf(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');
}
