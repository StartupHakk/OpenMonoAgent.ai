// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Common;
using Omabot.Team;

namespace Omabot.Secrets;

/// <summary>
/// Copies the secrets a team file gives a bot ("file:PATH" or "env:NAME")
/// into that bot's own folder, which is mounted read only at
/// /run/secrets/bot. Values never go into env or a command line.
/// </summary>
public static class BotSecrets
{
    public static IReadOnlyList<string> Materialize(BotSpec spec, RosterPaths paths, Redactor redactor, Func<string, string?>? readEnv = null)
    {
        readEnv ??= Environment.GetEnvironmentVariable;
        var problems = new List<string>();
        var dir = paths.Bot(spec.Slug).SecretsDir;
        SecureFile.EnsureDirNoClobber(paths.Secrets);
        SecureFile.EnsureDirNoClobber(paths.BotSecretsRoot);
        SecureFile.EnsureDirNoClobber(dir);
        foreach (var (name, reference) in spec.Secrets)
        {
            string? value = null;
            if (reference.StartsWith("file:", StringComparison.Ordinal))
            {
                var src = reference[5..];
                if (src.StartsWith("~/", StringComparison.Ordinal)) src = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), src[2..]);
                if (File.Exists(src)) value = File.ReadAllText(src);
                else problems.Add($"secret {name}: file not found");
            }
            else if (reference.StartsWith("env:", StringComparison.Ordinal))
            {
                value = readEnv(reference[4..]);
                if (string.IsNullOrEmpty(value)) problems.Add($"secret {name}: environment variable {reference[4..]} is not set");
            }
            else problems.Add($"secret {name}: reference must be file:PATH or env:NAME");
            if (value is null) continue;
            var why = SecretsGuard.Problem(value);
            if (why is not null) { problems.Add($"secret {name} {why}"); continue; }
            redactor.AddKnownSecret(value.Trim());
            SecureFile.WriteAllTextAtomic(Path.Combine(dir, name), SecureFile.NormalizeLf(value).TrimEnd('\n') + "\n");
        }
        // Secrets the team file gave earlier and no longer lists are removed.
        // Secrets stored with `oma-bot secrets set --bot` are not touched.
        var manifest = dir + ".team-secrets";
        if (File.Exists(manifest))
            foreach (var old in File.ReadAllLines(manifest).Where(l => l.Length > 0 && !spec.Secrets.ContainsKey(l)))
            {
                var f = Path.Combine(dir, Path.GetFileName(old));
                if (File.Exists(f)) File.Delete(f);
            }
        SecureFile.WriteAllTextAtomic(manifest, string.Join("\n", spec.Secrets.Keys) + "\n");
        return problems;
    }
}
