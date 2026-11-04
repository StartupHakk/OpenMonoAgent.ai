// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Omabot.Common;
using Omabot.Config;

namespace Omabot.Secrets;

public sealed record SecretInfo(string Name, string? Bot, string Path, string Mode, long Bytes, DateTimeOffset Updated);

public sealed record SecretSetResult(string Name, string? Bot, string Path, string Applies);

public sealed class SecretException(string message) : Exception(message);

/// <summary>
/// The local secrets folder (plan 7.12). Values come from a file or standard
/// input only, never from a command line argument. Files are 0600, folders
/// 0700, one folder per bot. Every value is registered with the redactor.
/// </summary>
public sealed partial class SecretStore(RosterPaths paths, RosterConfig config, Redactor redactor)
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")]
    private static partial Regex NamePattern();

    /// <summary>Store or replace one secret. Roster names: inference, backend-NAME, relay-NAME.</summary>
    public SecretSetResult Set(string name, string value, string? bot)
    {
        var path = PathFor(name, bot);
        var problem = SecretsGuard.Problem(value);
        if (problem is not null) throw new SecretException($"Secret {Label(name, bot)} {problem}; nothing was stored.");
        SecureFile.EnsureDirNoClobber(paths.Secrets);
        SecureFile.EnsureDirNoClobber(Path.GetDirectoryName(path)!);
        var clean = SecureFile.NormalizeLf(value).TrimEnd('\n') + "\n";
        redactor.AddKnownSecret(clean.Trim());
        foreach (var line in clean.Split('\n')) if (line.Trim().Length >= 12) redactor.AddKnownSecret(line.Trim());
        SecureFile.WriteAllTextAtomic(path, clean);
        var applies = bot is not null
            ? $"restart {bot} to apply: oma-bot down {bot} && oma-bot up {bot}"
            : name.StartsWith("relay-", StringComparison.Ordinal)
                ? "restart the FRPC client on this host to apply"
                : "the gateway reads the key on every request; no restart needed";
        return new SecretSetResult(name, bot, path, applies);
    }

    public bool Remove(string name, string? bot)
    {
        var path = PathFor(name, bot);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public List<SecretInfo> List(string? bot = null)
    {
        var list = new List<SecretInfo>();
        if (bot is not null)
        {
            Names.RequireSafeSlug(bot, "bot slug");
            var dir = paths.Bot(bot).SecretsDir;
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.Ordinal)) list.Add(Info(Path.GetFileName(f), bot, f));
            return list;
        }
        if (File.Exists(paths.InferenceKey)) list.Add(Info("inference", null, paths.InferenceKey));
        foreach (var b in config.Gateway.Backends)
        {
            var f = Resolve(b.KeyFile);
            if (File.Exists(f) && f != paths.InferenceKey) list.Add(Info("backend-" + b.Name, null, f));
        }
        var relay = Path.Combine(paths.Secrets, "relay");
        if (Directory.Exists(relay))
            foreach (var f in Directory.EnumerateFiles(relay).OrderBy(f => f, StringComparer.Ordinal)) list.Add(Info("relay-" + Path.GetFileName(f), null, f));
        return list;
    }

    /// <summary>Register every stored secret value with the redactor (service and CLI start).</summary>
    public int LoadAllIntoRedactor()
    {
        if (!Directory.Exists(paths.Secrets)) return 0;
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(paths.Secrets, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(f);
            if (info.Length > 64 * 1024) continue;
            var text = File.ReadAllText(f);
            if (text.Trim().Length >= 8) { redactor.AddKnownSecret(text.Trim()); n++; }
            foreach (var line in text.Split('\n')) if (line.Trim().Length >= 12) redactor.AddKnownSecret(line.Trim());
        }
        return n;
    }

    public string PathFor(string name, string? bot)
    {
        if (!NamePattern().IsMatch(name)) throw new SecretException("Secret names are letters, digits, dashes and underscores, up to 64 characters.");
        if (bot is not null)
        {
            Names.RequireSafeSlug(bot, "bot slug");
            return Path.Combine(paths.Bot(bot).SecretsDir, name);
        }
        if (name == "inference") return Resolve(config.Gateway.UpstreamKeyFile);
        if (name.StartsWith("backend-", StringComparison.Ordinal))
        {
            var b = config.Gateway.Backends.FirstOrDefault(x => x.Name == name["backend-".Length..])
                ?? throw new SecretException($"No backend named {name["backend-".Length..]} in roster.json.");
            return Resolve(b.KeyFile);
        }
        if (name.StartsWith("relay-", StringComparison.Ordinal) && name.Length > 6) return Path.Combine(paths.Secrets, "relay", name[6..]);
        throw new SecretException("Roster secrets are inference, backend-NAME or relay-NAME. Bot secrets need --bot BOT.");
    }

    private string Resolve(string file)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(paths.Home, file));
        var root = Path.GetFullPath(paths.Secrets) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.Ordinal)) throw new SecretException("Key files must live under the roster secrets folder.");
        return full;
    }

    private static string Label(string name, string? bot) => bot is null ? name : $"{name} for {bot}";

    private static SecretInfo Info(string name, string? bot, string path)
    {
        var fi = new FileInfo(path);
        var mode = SecureFile.ModeOf(path) is { } m ? Convert.ToString((int)m, 8).PadLeft(4, '0') : "n/a";
        return new SecretInfo(name, bot, path, mode, fi.Length, fi.LastWriteTimeUtc);
    }
}
