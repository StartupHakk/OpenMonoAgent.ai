using System.Text.Json.Serialization;

namespace OpenMono.Config;

/// <summary>
/// Git auth profiles (settings.json "git" section). A profile names either an
/// SSH key (under ~/.ssh, the directory the container launcher mounts read-only)
/// or an HTTPS personal access token (literal or via an env-var name).
/// Selection: OPENMONO_GIT_PROFILE (per session) → git.default → none
/// (legacy host pass-through; no git env vars are set at all).
/// </summary>
public sealed class GitConfig
{
    public string? Default { get; set; }
    public Dictionary<string, GitProfile> Profiles { get; set; } = [];

    // Transient: set from OPENMONO_GIT_PROFILE at load time, never serialized
    // or merged from files (a session selection must not leak into settings).
    [JsonIgnore]
    public string? SessionProfile { get; set; }

    public void MergeFrom(GitConfig source)
    {
        if (!string.IsNullOrEmpty(source.Default)) Default = source.Default;

        foreach (var (name, profile) in source.Profiles)
        {
            if (!Profiles.TryGetValue(name, out var existing))
            {
                Profiles[name] = profile;
                continue;
            }
            existing.MergeFrom(profile);
        }
    }

    /// <summary>
    /// Resolve which profile this session should use. Returns (profile, error):
    /// error is set when a name was requested but unknown/malformed — callers
    /// should surface it as a startup warning and keep legacy pass-through.
    /// </summary>
    public (GitProfile? Profile, string? Error) ResolveSelectedProfile()
    {
        var name = SessionProfile ?? Default;
        if (string.IsNullOrWhiteSpace(name))
            return (null, null);

        if (!Profiles.TryGetValue(name, out var profile))
            return (null, $"git profile '{name}' not found (known: {string.Join(", ", Profiles.Keys)})");

        if (string.IsNullOrWhiteSpace(profile.Type) ||
            (profile.Type != "ssh" && profile.Type != "https"))
            return (null, $"git profile '{name}' has invalid type '{profile.Type ?? ""}' (expected 'ssh' or 'https')");

        return (profile, null);
    }
}

public sealed class GitProfile
{
    // "ssh" | "https"
    public string? Type { get; set; }

    // ssh: path to the private key (must live under ~/.ssh — the mounted dir).
    public string? SshKey { get; set; }

    // https: literal token. Prefer TokenEnv to keep the secret out of the file.
    public string? Token { get; set; }

    // https: name of the env var holding the token (read at startup).
    public string? TokenEnv { get; set; }

    // https: host the credentials apply to (default github.com).
    public string? Host { get; set; }

    // git identity applied for the session (user.name / user.email).
    public string? User { get; set; }
    public string? Email { get; set; }

    public void MergeFrom(GitProfile source)
    {
        if (!string.IsNullOrEmpty(source.Type)) Type = source.Type;
        if (!string.IsNullOrEmpty(source.SshKey)) SshKey = source.SshKey;
        if (!string.IsNullOrEmpty(source.Token)) Token = source.Token;
        if (!string.IsNullOrEmpty(source.TokenEnv)) TokenEnv = source.TokenEnv;
        if (!string.IsNullOrEmpty(source.Host)) Host = source.Host;
        if (!string.IsNullOrEmpty(source.User)) User = source.User;
        if (!string.IsNullOrEmpty(source.Email)) Email = source.Email;
    }
}
