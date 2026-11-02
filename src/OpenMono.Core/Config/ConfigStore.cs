using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenMono.Config;

/// <summary>
/// Write path for the user-level settings file (~/.openmono/settings.json).
/// The loader is read-only; this is the one place a runtime command (e.g. the
/// /git add wizard) can persist a setting. It edits the "git" section of the
/// existing JSON in place, preserving every other section and any comments the
/// user may have added, and never touches the git.default / session-selection
/// keys — only git.profiles.
/// </summary>
public static class ConfigStore
{
    public static string UserSettingsPath(AppConfig config)
        => Path.Combine(config.DataDirectory, "settings.json");

    /// <summary>
    /// Persist a profile under git.profiles in the user-level settings file.
    /// Existing top-level sections are preserved; an existing profile of the
    /// same name is replaced.
    /// </summary>
    public static void UpsertGitProfile(AppConfig config, string name, GitProfile profile)
    {
        var path = UserSettingsPath(config);
        Directory.CreateDirectory(config.DataDirectory);
        var isNewFile = !File.Exists(path);

        var root = LoadRoot(path);
        var git = root["git"] as JsonObject ?? new JsonObject();
        var profiles = git["profiles"] as JsonObject ?? new JsonObject();

        var node = new JsonObject();
        node["type"] = profile.Type;
        if (!string.IsNullOrEmpty(profile.SshKey)) node["ssh_key"] = profile.SshKey;
        if (!string.IsNullOrEmpty(profile.Token)) node["token"] = profile.Token;
        if (!string.IsNullOrEmpty(profile.TokenEnv)) node["token_env"] = profile.TokenEnv;
        if (!string.IsNullOrEmpty(profile.Host)) node["host"] = profile.Host;
        if (!string.IsNullOrEmpty(profile.User)) node["user"] = profile.User;
        if (!string.IsNullOrEmpty(profile.Email)) node["email"] = profile.Email;

        profiles[name] = node;
        git["profiles"] = profiles;
        root["git"] = git;

        File.WriteAllText(path, root.ToJsonString(JsonOptions.Indented));
        // A freshly created settings file may soon hold a PAT — lock it down.
        // Never touch the mode of a pre-existing file (it may be managed elsewhere).
        if (isNewFile && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static JsonObject LoadRoot(string path)
    {
        if (!File.Exists(path))
            return new JsonObject();

        // The existing file may contain keys this build doesn't model (or a
        // malformed value under one) — deserialize into a node, not a typed
        // object, so the save can never silently drop user settings.
        var node = JsonNode.Parse(File.ReadAllText(path));
        if (node is JsonObject obj)
            return obj;

        throw new JsonException(
            "settings.json does not contain a JSON object at the top level — refusing to overwrite it; edit it manually (see docs/GIT-AUTH.md)");
    }
}
