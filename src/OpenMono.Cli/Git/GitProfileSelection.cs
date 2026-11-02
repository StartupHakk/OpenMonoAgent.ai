using OpenMono.Config;
using OpenMono.Session;

namespace OpenMono.Cli.Git;

/// <summary>
/// Resolves which git auth profile a session should start with.
/// Precedence:
///   1. the session's own persisted choice (Meta.GitProfileName + GitProfileTouched —
///      the last explicit /git &lt;name&gt; | /git off, or ACP POST /git) — so a resumed
///      session keeps what the user chose, including an explicit "off" that must not
///      fall back to git.default;
///   2. the launch-time env var (GitConfig.SessionProfile from OPENMONO_GIT_PROFILE);
///   3. git.default (project → user settings merge);
///   4. nothing → legacy host pass-through (null).
/// A persisted (or launch-time) name that no longer exists, or is malformed, in
/// the current config is reported via <see cref="Warning"/> and degrades to
/// legacy host pass-through — it never silently switches to a DIFFERENT profile,
/// which could push credentials the user did not ask for.
/// </summary>
public static class GitProfileSelection
{
    public static (string? ProfileName, string? Warning) Resolve(GitConfig git, SessionState? session)
    {
        if (session is { Meta.GitProfileTouched: true })
        {
            var name = session.Meta.GitProfileName;
            if (string.IsNullOrEmpty(name))
                return (null, null); // explicit "off" — host pass-through
            if (TryValidate(git, name, out var err))
                return (name, null);
            return (null, $"git profile '{name}' (saved with this session) no longer exists in settings.json — continuing with host git credentials");
        }

        var fallback = git.SessionProfile ?? git.Default;
        if (string.IsNullOrWhiteSpace(fallback))
            return (null, null);
        if (TryValidate(git, fallback, out string? fallbackError))
            return (fallback, null);
        return (null, $"{fallbackError} — continuing with host git credentials");
    }

    private static bool TryValidate(GitConfig git, string name, out string? error)
    {
        if (!git.Profiles.TryGetValue(name, out var profile))
        {
            error = $"git profile '{name}' not found (known: {string.Join(", ", git.Profiles.Keys)})";
            return false;
        }
        if (string.IsNullOrWhiteSpace(profile.Type) ||
            (profile.Type != "ssh" && profile.Type != "https"))
        {
            error = $"git profile '{name}' has invalid type '{profile.Type ?? ""}' (expected 'ssh' or 'https')";
            return false;
        }
        error = null;
        return true;
    }
}
