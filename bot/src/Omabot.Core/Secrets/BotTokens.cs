// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Common;

namespace Omabot.Secrets;

/// <summary>
/// Per-bot gateway tokens in secrets/bots/&lt;slug&gt;.token (0600). The value
/// is passed to the container env at launch (builder choice B12) and is
/// registered with the redactor so it never reaches a log.
/// </summary>
public sealed class BotTokens(RosterPaths paths, Redactor redactor)
{
    public string Ensure(string slug)
    {
        Names.RequireSafeSlug(slug, "bot slug");
        var file = paths.Bot(slug).TokenFile;
        SecureFile.EnsureDirNoClobber(paths.Secrets);
        SecureFile.EnsureDirNoClobber(paths.BotSecretsRoot);
        if (!File.Exists(file)) SecureFile.WriteIfMissing(file, SecureFile.NewToken() + "\n");
        var token = File.ReadAllText(file).Trim();
        redactor.AddKnownSecret(token);
        return token;
    }

    /// <summary>Replace a bot token. The bot must be restarted to pick it up.</summary>
    public string Rotate(string slug)
    {
        Names.RequireSafeSlug(slug, "bot slug");
        var file = paths.Bot(slug).TokenFile;
        if (File.Exists(file)) redactor.AddKnownSecret(File.ReadAllText(file).Trim());
        var token = SecureFile.NewToken();
        SecureFile.WriteAllTextAtomic(file, token + "\n");
        redactor.AddKnownSecret(token);
        return token;
    }

    /// <summary>Map of sha256(token) to slug, for the gateway. Raw tokens are not kept in the map.</summary>
    public Dictionary<string, string> LoadHashes()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(paths.BotSecretsRoot)) return map;
        foreach (var f in Directory.EnumerateFiles(paths.BotSecretsRoot, "*.token"))
        {
            var slug = Path.GetFileNameWithoutExtension(f);
            if (!Names.IsSafeSlug(slug)) continue;
            var token = File.ReadAllText(f).Trim();
            redactor.AddKnownSecret(token);
            map[SecureFile.Sha256Hex(token)] = slug;
        }
        return map;
    }
}
