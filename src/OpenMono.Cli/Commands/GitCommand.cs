using OpenMono.Config;
using OpenMono.Utils;

namespace OpenMono.Commands;

/// <summary>
/// Shows the configured git auth profiles and switches the session's active
/// profile at runtime. The selected profile is layered onto every Bash child at
/// spawn time, so the switch takes effect on the next git command — no restart.
///   <c>/git</c>               — list configured profiles, highlighting the active one
///   <c>/git &lt;name&gt;</c>     — switch to that profile
///   <c>/git off</c>           — clear the profile (legacy host pass-through)
///   <c>/git add</c>           — wizard: create a new profile in settings.json
///   <c>/git add &lt;name&gt;</c> — same, pre-filled profile name
/// </summary>
public sealed class GitCommand : ICommand
{
    public string Name => "git";
    public string Description => "List git auth profiles, switch the active one, or add a new one (/git add)";
    public CommandType Type => CommandType.Local;

    public async Task ExecuteAsync(string[] args, CommandContext context, CancellationToken ct)
    {
        var git = context.Config.Git;
        var arg = args.Length > 0 ? string.Join(' ', args).Trim() : "";

        if (arg.StartsWith("add", StringComparison.OrdinalIgnoreCase))
        {
            var rest = arg[3..].Trim();
            var prefill = rest.Length > 0 ? rest : null;
            await RunAddWizardAsync(context, prefill, ct);
            return;
        }

        if (string.IsNullOrEmpty(arg))
        {
            var current = context.Session.Meta.GitProfileName;
            context.Renderer.WriteInfo($"Current git profile: {(current ?? "(host pass-through)")}");
            if (git.Profiles.Count == 0)
            {
                context.Renderer.WriteInfo("No profiles configured. Create one with: /git add");
                return;
            }

            context.Renderer.WriteInfo("");
            context.Renderer.WriteInfo("Configured profiles:");
            foreach (var (name, prof) in git.Profiles)
            {
                var marker = string.Equals(name, current, StringComparison.Ordinal) ? "→" : " ";
                var kind = string.IsNullOrWhiteSpace(prof.Type) ? "?" : prof.Type;
                var detail = prof.Type == "ssh"
                    ? (string.IsNullOrWhiteSpace(prof.SshKey) ? "" : prof.SshKey)
                    : prof.Type == "https"
                        ? (string.IsNullOrWhiteSpace(prof.Host) ? "github.com" : prof.Host)
                        : "";
                context.Renderer.WriteInfo($"  {marker} {name}  [{kind}]{(detail.Length > 0 ? $" — {detail}" : "")}");
            }
            context.Renderer.WriteInfo("");
            context.Renderer.WriteInfo("Switch with: /git <name>   or   /git off   ·   New profile: /git add");
            return;
        }

        if (arg.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            context.Session.Meta.GitProfileName = null;
            context.Session.Meta.GitProfileTouched = true;
            Log.Info("git auth: profile cleared (host pass-through)");
            context.Renderer.WriteInfo("✓ Git profile cleared — git commands now use the host's own credentials.");
            return;
        }

        if (!git.Profiles.TryGetValue(arg, out var profile))
        {
            context.Renderer.WriteWarning(
                $"'{arg}' is not a configured git profile (known: {string.Join(", ", git.Profiles.Keys)}).");
            return;
        }

        if (string.IsNullOrWhiteSpace(profile.Type) || (profile.Type != "ssh" && profile.Type != "https"))
        {
            context.Renderer.WriteWarning($"git profile '{arg}' has invalid type '{profile.Type ?? ""}' (expected 'ssh' or 'https').");
            return;
        }

        context.Session.Meta.GitProfileName = arg;
        context.Session.Meta.GitProfileTouched = true;
        Log.Info($"git auth: switched to profile '{arg}' ({profile.Type})");
        context.Renderer.WriteInfo($"✓ Git profile switched to '{arg}' — takes effect on your next git command.");
    }

    /// <summary>
    /// The /git add wizard: walks the user through one profile (name, ssh vs
    /// https, key or token, identity) and persists it to the user-level
    /// settings.json — the same file ConfigLoader reads, with every other
    /// section preserved. Validation mirrors GitAuthApplier so a profile the
    /// wizard writes can never be one the applier would reject.
    /// </summary>
    private static async Task RunAddWizardAsync(CommandContext context, string? prefill, CancellationToken ct)
    {
        var renderer = context.Renderer;
        if (!renderer.Interactive)
        {
            renderer.WriteWarning(
                "The /git add wizard needs an interactive terminal. Hand-edit ~/.openmono/settings.json " +
                "(a full example is in docs/GIT-AUTH.md), then /git <name> to activate it.");
            return;
        }

        var git = context.Config.Git;
        renderer.WriteInfo("New git auth profile — press Enter to accept the default in [brackets].");

        // 1. Name
        var name = "";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            name = (await renderer.AskUserAsync(
                $"Profile name [{prefill ?? SuggestName(git)}]", ct)).Trim();
            if (name.Length == 0) name = prefill ?? SuggestName(git); // accept the default
            if (IsValidName(name)) break;
            renderer.WriteWarning("Use letters, digits, '-' or '_' (no spaces).");
        }
        while (git.Profiles.ContainsKey(name))
        {
            renderer.WriteInfo($"'{name}' already exists.");
            var overwrite = await renderer.AskUserAsync("Overwrite it? [y/N]", ct);
            if (overwrite.Equals("y", StringComparison.OrdinalIgnoreCase))
                break;
            ct.ThrowIfCancellationRequested();
            name = (await renderer.AskUserAsync("Profile name [new]", ct)).Trim();
            if (name.Length == 0 || !IsValidName(name))
            {
                renderer.WriteInfo("Cancelled.");
                return;
            }
        }

        // 2. Type
        renderer.WriteInfo("");
        renderer.WriteInfo("1) SSH — name an existing private key under ~/.ssh");
        renderer.WriteInfo("2) HTTPS — personal access token (GitHub / GitLab / Gitea / any HTTPS git host)");
        var type = "ssh";
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var t = (await renderer.AskUserAsync("Authentication type [1]", ct)).Trim();
            if (t.Length == 0 || t == "1" || t.Equals("ssh", StringComparison.OrdinalIgnoreCase)) { type = "ssh"; break; }
            if (t == "2" || t.Equals("https", StringComparison.OrdinalIgnoreCase) || t.Equals("pat", StringComparison.OrdinalIgnoreCase)) { type = "https"; break; }
            renderer.WriteWarning("Enter 1 (ssh) or 2 (https).");
        }

        var profile = new GitProfile { Type = type };

        if (type == "ssh")
        {
            var existing = ListSshKeys();
            var defaultKey = (prefill is null && existing.Count == 1) ? existing[0] : "";
            var keyHint = existing.Count > 0
                ? string.Join(", ", existing)
                : "e.g. ~/.ssh/id_ed25519 (the key must live under ~/.ssh)";
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var answer = await renderer.AskUserAsync($"SSH private key [{keyHint}]", ct);
                var key = answer.Trim();
                if (key.Length == 0)
                {
                    if (defaultKey.Length > 0)
                        key = defaultKey;
                    else
                    {
                        renderer.WriteInfo("Cancelled.");
                        return;
                    }
                }
                var (normalized, error) = NormalizeSshKey(key);
                if (normalized is null)
                {
                    renderer.WriteWarning(error!);
                    continue;
                }
                profile.SshKey = normalized;
                break;
            }
        }
        else
        {
            var host = "";
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                host = (await renderer.AskUserAsync("Git host [github.com]", ct)).Trim();
                if (host.Length == 0) { host = "github.com"; break; }
                if (Uri.TryCreate($"https://{host}/", UriKind.Absolute, out _)) break;
                renderer.WriteWarning("Enter a plain hostname, e.g. github.com or gitlab.example.com.");
            }
            profile.Host = host;

            renderer.WriteInfo("");
            renderer.WriteInfo("1) Name of an environment variable holding the token (recommended — the secret");
            renderer.WriteInfo("   never touches settings.json; the variable must be set before launch)");
            renderer.WriteInfo("2) Paste the token now (stored in settings.json, file locked to 0600)");
            var secretChoice = "1";
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var c = (await renderer.AskUserAsync("Token source [1]", ct)).Trim();
                if (c.Length == 0 || c == "1" || c.Equals("env", StringComparison.OrdinalIgnoreCase)) { secretChoice = "1"; break; }
                if (c == "2" || c.Equals("paste", StringComparison.OrdinalIgnoreCase) || c.Equals("token", StringComparison.OrdinalIgnoreCase)) { secretChoice = "2"; break; }
                renderer.WriteWarning("Enter 1 (env var) or 2 (paste).");
            }

            if (secretChoice == "1")
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var env = (await renderer.AskUserAsync("Environment variable name [GITHUB_PAT]", ct)).Trim();
                    if (env.Length == 0) env = "GITHUB_PAT";
                    if (IsValidEnvName(env)) { profile.TokenEnv = env; break; }
                    renderer.WriteWarning("Env var names use letters, digits and '_' (must start with a letter).");
                }
            }
            else
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var secret = await renderer.AskSecretAsync(
                        "Paste the personal access token (masked)", ct);
                    var token = new string(secret).Trim();
                    for (var i = 0; i < secret.Length; i++) secret[i] = '\0';
                    if (token.Length == 0)
                    {
                        renderer.WriteInfo("Cancelled.");
                        return;
                    }
                    if (token.Length >= 8) { profile.Token = token; break; }
                    renderer.WriteWarning("That looks too short for a PAT — re-enter it, or use an env var (1).");
                }
            }
        }

        // 3. Identity — prefilled from the host's git config so the common
        //    case (one human identity) is two Enters.
        renderer.WriteInfo("");
        var (hostName, hostEmail) = await ReadHostIdentityAsync(ct);
        ct.ThrowIfCancellationRequested();
        var u = (await renderer.AskUserAsync($"user.name [{hostName ?? "Jane Dev"}]", ct)).Trim();
        profile.User = u.Length > 0 ? u : hostName ?? "Jane Dev";
        ct.ThrowIfCancellationRequested();
        var e = (await renderer.AskUserAsync($"user.email [{hostEmail ?? "jane@example.com"}]", ct)).Trim();
        profile.Email = e.Length > 0 ? e : hostEmail ?? "jane@example.com";

        // 4. Confirm + persist
        renderer.WriteInfo("");
        renderer.WriteInfo($"Will save to {ConfigStore.UserSettingsPath(context.Config)}:");
        renderer.WriteMarkdown(BuildProfileMarkdown(name, profile));
        var confirm = await renderer.AskUserAsync("Save this profile? [Y/n]", ct);
        if (confirm.Equals("n", StringComparison.OrdinalIgnoreCase))
        {
            renderer.WriteInfo("Cancelled — nothing saved.");
            return;
        }

        try
        {
            ConfigStore.UpsertGitProfile(context.Config, name, profile);
        }
        catch (Exception ex)
        {
            renderer.WriteWarning($"Could not save settings.json: {ex.Message}");
            renderer.WriteInfo("You can edit it manually — docs/GIT-AUTH.md has the exact schema.");
            return;
        }

        // Hot-swap the live config so /git can select the new profile now,
        // without a restart.
        git.Profiles[name] = profile;

        var activate = await renderer.AskUserAsync($"Activate '{name}' for this session now? [Y/n]", ct);
        if (!activate.Equals("n", StringComparison.OrdinalIgnoreCase))
        {
            context.Session.Meta.GitProfileName = name;
            context.Session.Meta.GitProfileTouched = true;
        }
        Log.Info($"git auth: profile '{name}' ({profile.Type}) saved via /git add");
        renderer.WriteInfo($"✓ Profile '{name}' saved{(activate.Equals("n", StringComparison.OrdinalIgnoreCase) ? "" : $" and active for this session")}.");
    }

    private static string SuggestName(GitConfig git)
        => git.Profiles.Count == 0 ? "default" : $"{git.Profiles.Count + 1}";

    private static bool IsValidName(string name) =>
        name.Length > 0 && name.Length <= 32 &&
        name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_');

    private static bool IsValidEnvName(string name) =>
        name.Length > 0 && char.IsLetter(name[0]) &&
        name.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>
    /// Validate an SSH key path against the same rule GitAuthApplier enforces
    /// (must live under ~/.ssh) and normalize it to the "~/.ssh/&lt;name&gt;" form
    /// the profile stores — so a bare filename typed at the prompt round-trips
    /// through the applier.
    /// </summary>
    private static (string? Normalized, string? Error) NormalizeSshKey(string input)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expanded = input;
        if (expanded.StartsWith("~/", StringComparison.Ordinal))
            expanded = Path.Combine(home, expanded[2..]);
        else if (!Path.IsPathRooted(expanded))
            expanded = Path.Combine(home, ".ssh", expanded);

        var sshDir = Path.TrimEndingDirectorySeparator(Path.Combine(home, ".ssh"));
        if (!expanded.StartsWith(sshDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return (null, $"the key must live under ~/.ssh (the launcher mounts that directory read-only)");
        if (!File.Exists(expanded))
            return (null, $"no such file: {input} — create the key first (or check the path)");

        var rel = expanded[(sshDir.Length + 1)..].Replace('\\', '/');
        return ($"~/.ssh/{rel}", null);
    }

    /// <summary>Existing private keys under ~/.ssh, as "~/.ssh/&lt;name&gt;" paths.</summary>
    private static List<string> ListSshKeys()
    {
        var sshDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (!Directory.Exists(sshDir))
            return [];
        try
        {
            return Directory.EnumerateFiles(sshDir)
                .Select(Path.GetFileName)
                .Where(n => n is not null && !n.StartsWith('.')
                            && !n.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => $"~/.ssh/{n}")
                .ToList()!;
        }
        catch
        {
            return [];
        }
    }

    private static async Task<(string? Name, string? Email)> ReadHostIdentityAsync(CancellationToken ct)
    {
        string? name = null, email = null;
        try
        {
            var (e1, o1, _) = await ProcessRunner.RunAsync("git config --get user.name 2>/dev/null", timeoutMs: 5_000, ct: ct);
            if (e1 == 0 && !string.IsNullOrWhiteSpace(o1)) name = o1;
            var (e2, o2, _) = await ProcessRunner.RunAsync("git config --get user.email 2>/dev/null", timeoutMs: 5_000, ct: ct);
            if (e2 == 0 && !string.IsNullOrWhiteSpace(o2)) email = o2;
        }
        catch { /* identity prefill is best-effort */ }
        return (name, email);
    }

    private static string BuildProfileMarkdown(string name, GitProfile profile)
    {
        var lines = new List<string>
        {
            "```json",
            "\"git\": {",
            "  \"profiles\": {",
            $"    \"{name}\": {{",
            $"      \"type\": \"{profile.Type}\",",
        };
        if (!string.IsNullOrEmpty(profile.SshKey)) lines.Add($"      \"ssh_key\": \"{profile.SshKey}\",");
        if (!string.IsNullOrEmpty(profile.Host)) lines.Add($"      \"host\": \"{profile.Host}\",");
        if (!string.IsNullOrEmpty(profile.TokenEnv)) lines.Add($"      \"token_env\": \"{profile.TokenEnv}\",");
        if (!string.IsNullOrEmpty(profile.Token)) lines.Add("      \"token\": \"(hidden — 0600 file)\",");
        lines.Add($"      \"user\": \"{profile.User}\",");
        lines.Add($"      \"email\": \"{profile.Email}\"");
        lines.Add("    }");
        lines.Add("  }");
        lines.Add("}");
        lines.Add("```");
        return string.Join('\n', lines);
    }
}
