using System.Diagnostics;
using OpenMono.Config;

namespace OpenMono.Cli.Git;

/// <summary>
/// Resolves a git auth profile (settings.json "git" section) into the env vars
/// a git child needs to authenticate as it, and layers them onto a
/// <see cref="ProcessStartInfo"/>.
///
/// HTTPS: git's env-level config (GIT_CONFIG_COUNT/KEY/VALUE) supplies
/// credential.https://&lt;host&gt;.username / .password plus identity, and
/// credential.helper=!true so a host helper (store/cache/osxkeychain) can't
/// shadow the config-level credentials.
///
/// SSH: GIT_SSH_COMMAND is overridden with -i &lt;key&gt; + IdentitiesOnly so
/// exactly that key is offered (it must live under ~/.ssh — the launcher
/// mounts that directory read-only into the container).
///
/// The launcher already exports GIT_CONFIG_COUNT=1 (safe.directory), so the
/// resolver APPENDS to any inherited triplets instead of clobbering them.
///
/// Per-session: the profile NAME lives on the session (SessionMetadata
/// GitProfileName), and <see cref="ApplyTo"/> layers the resolved env onto each
/// Bash child at spawn time. That keeps multi-session ACP from sharing one
/// process-global git identity, and makes the selection hot-swappable (TUI
/// /git, ACP POST /sessions/{id}/git) without restarting the process. A null
/// name is a no-op → legacy host pass-through.
/// </summary>
public static class GitAuthApplier
{
    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>();

    /// <summary>
    /// Resolve the session's selected profile (SessionProfile → Default) into env
    /// vars. No profile → empty dict (legacy host pass-through); a requested-but-
    /// unknown/malformed name throws (startup callers surface it as a warning).
    /// </summary>
    public static IReadOnlyDictionary<string, string> ResolveEnvVars(GitConfig config)
    {
        var (profile, error) = config.ResolveSelectedProfile();
        if (error is not null)
            throw new ArgumentException($"git auth: {error}", nameof(config));
        if (profile is null)
            return Empty;
        return BuildEnvVars(profile);
    }

    /// <summary>
    /// Resolve an explicit profile name against the config. Returns empty on null
    /// or unknown/malformed — the per-child and hot-swap paths treat "no valid
    /// selection" as a silent no-op (legacy pass-through) rather than an error, so
    /// a mid-session swap to a bad name degrades gracefully instead of failing
    /// every git command.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ResolveEnvVarsFor(string? name, GitConfig git)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Empty;
        if (!git.Profiles.TryGetValue(name!, out var profile))
            return Empty;
        if (string.IsNullOrWhiteSpace(profile.Type) ||
            (profile.Type != "ssh" && profile.Type != "https"))
            return Empty;
        return BuildEnvVars(profile);
    }

    /// <summary>
    /// Layer the named profile's env onto <paramref name="psi"/> for one child.
    /// Inherited process env (including the launcher's safe.directory triplet) is
    /// preserved — the child's env starts as a copy of the process env, and we only
    /// add/override the git keys on top. Null name, unknown name, or a name whose
    /// profile fails to resolve (e.g. token_env unset) is a no-op so the child falls
    /// back to the host's own credentials instead of failing to launch.
    /// </summary>
    public static void ApplyTo(ProcessStartInfo psi, string? name, GitConfig git)
    {
        IReadOnlyDictionary<string, string> vars;
        try
        {
            vars = ResolveEnvVarsFor(name, git);
        }
        catch (ArgumentException)
        {
            vars = Empty;
        }
        foreach (var (key, value) in vars)
            psi.Environment[key] = value;
    }

    /// <summary>
    /// The single profile→env computation shared by the startup and per-child
    /// paths: start from whatever the launcher already exported, drop any host
    /// credential helper, then add this profile's credential + identity entries.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildEnvVars(GitProfile profile)
    {
        var entries = new List<(string Key, string Value)>();

        // Start from whatever the launcher already exported.
        if (int.TryParse(Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT"), out var count) && count > 0)
        {
            for (var i = 0; i < count; i++)
            {
                var k = Environment.GetEnvironmentVariable($"GIT_CONFIG_KEY_{i}");
                var v = Environment.GetEnvironmentVariable($"GIT_CONFIG_VALUE_{i}");
                if (k is not null)
                    entries.Add((k, v ?? string.Empty));
            }
        }

        // Any host-level credential helper in the environment (e.g. a
        // launcher-supplied credential.helper) would otherwise shadow the
        // config-level credentials below — drop it from the re-exported set.
        for (var i = entries.Count - 1; i >= 0; i--)
            if (entries[i].Key == "credential.helper")
                entries.RemoveAt(i);

        string? sshCommand = null;

        switch (profile.Type)
        {
            case "https":
            {
                var host = string.IsNullOrWhiteSpace(profile.Host) ? "github.com" : profile.Host!.Trim();
                var token = ResolveToken(profile, host);
                entries.Add(("credential.helper", "!true"));
                entries.Add(($"credential.https://{host}.username", profile.User ?? "git"));
                entries.Add(($"credential.https://{host}.password", token));
                break;
            }
            case "ssh":
            {
                var key = ResolveSshKey(profile);
                sshCommand =
                    $"ssh -i {key} -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=accept-new";
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(profile.User))
            entries.Add(("user.name", profile.User!));
        if (!string.IsNullOrWhiteSpace(profile.Email))
            entries.Add(("user.email", profile.Email!));

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        result["GIT_CONFIG_COUNT"] = entries.Count.ToString();
        for (var i = 0; i < entries.Count; i++)
        {
            result[$"GIT_CONFIG_KEY_{i}"] = entries[i].Key;
            result[$"GIT_CONFIG_VALUE_{i}"] = entries[i].Value;
        }
        if (sshCommand is not null)
            result["GIT_SSH_COMMAND"] = sshCommand;

        return result;
    }

    private static string ResolveToken(GitProfile profile, string host)
    {
        if (!string.IsNullOrEmpty(profile.TokenEnv))
        {
            var fromEnv = Environment.GetEnvironmentVariable(profile.TokenEnv);
            if (string.IsNullOrEmpty(fromEnv))
                throw new ArgumentException(
                    $"git auth: token env var '{profile.TokenEnv}' is not set — " +
                    $"export it before starting the agent");
            return fromEnv;
        }
        if (!string.IsNullOrEmpty(profile.Token))
            return profile.Token;
        throw new ArgumentException(
            $"git auth: https profile has neither 'token' nor 'token_env' set (host {host})");
    }

    private static string ResolveSshKey(GitProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.SshKey))
            throw new ArgumentException("git auth: ssh profile has no 'ssh_key' set");

        var expanded = profile.SshKey!;
        if (expanded.StartsWith("~/", StringComparison.Ordinal))
            expanded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded[2..]);

        var sshDir = Path.TrimEndingDirectorySeparator(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"));
        if (!expanded.StartsWith(sshDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"git auth: ssh_key '{expanded}' must live under ~/.ssh (the launcher mounts that " +
                "directory read-only into the container)");
        }
        return expanded;
    }
}
