// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Omabot.Common;

/// <summary>
/// The roster home layout from plan section 6.5. All paths are absolute.
/// In multi-user mode the same layout repeats under users/&lt;user&gt;/.
/// </summary>
public sealed class RosterPaths
{
    public RosterPaths(string home)
    {
        if (string.IsNullOrWhiteSpace(home)) throw new ArgumentException("Roster home is empty.", nameof(home));
        Home = Path.GetFullPath(home);
    }

    public static string DefaultHome()
    {
        var env = Environment.GetEnvironmentVariable("OMABOT_HOME");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".omabot");
    }

    public string Home { get; }
    public string Db => Path.Combine(Home, "roster.db");
    public string ConfigFile => Path.Combine(Home, "roster.json");
    public string Secrets => Path.Combine(Home, "secrets");
    public string InferenceKey => Path.Combine(Secrets, "inference.key");
    public string BotSecretsRoot => Path.Combine(Secrets, "bots");
    public string Logs => Path.Combine(Home, "logs");
    public string Run => Path.Combine(Home, "run");
    public string RosterSocket => Path.Combine(Run, "roster.sock");
    public string SupervisorSocket => Path.Combine(Run, "supervisor.sock");
    public string SupervisorToken => Path.Combine(Secrets, "supervisor.token");
    public string ApiToken => Path.Combine(Secrets, "api.token");
    public string Web => Path.Combine(Home, "web");
    public string UsersDb => Path.Combine(Web, "users.db");
    public string WebSessions => Path.Combine(Web, "sessions");
    public string WebEnroll => Path.Combine(Web, "enroll");
    public string WebCheckNonce => Path.Combine(Web, "check.nonce");
    public string WebAdminSecret => Path.Combine(Secrets, "web-admin.secret");
    public string Bots => Path.Combine(Home, "bots");
    public string Archive => Path.Combine(Home, "archive");
    public string UsersRoot => Path.Combine(Home, "users");

    public RosterPaths ForUser(string user)
    {
        Names.RequireSafeSlug(user, "user");
        return new RosterPaths(Path.Combine(UsersRoot, user));
    }

    public BotPaths Bot(string slug) => new(this, slug);
}

/// <summary>Folders and files for one bot.</summary>
public sealed class BotPaths
{
    internal BotPaths(RosterPaths roster, string slug)
    {
        Names.RequireSafeSlug(slug, "bot");
        Slug = slug;
        Root = Path.Combine(roster.Bots, slug);
        TokenFile = Path.Combine(roster.BotSecretsRoot, slug + ".token");
        SecretsDir = Path.Combine(roster.BotSecretsRoot, slug);
        ShimSocketDir = Path.Combine(roster.Run, "bots", slug);
    }

    public string Slug { get; }
    public string Root { get; }
    public string BotYaml => Path.Combine(Root, "bot.yaml");
    public string Data => Path.Combine(Root, "data");
    public string Settings => Path.Combine(Data, "settings.json");
    public string SystemPrompt => Path.Combine(Data, "system-prompt.md");
    public string PlanPrompt => Path.Combine(Data, "plan-prompt.md");
    public string DefaultWorkspace => Path.Combine(Root, "workspace");
    public string Journal => Path.Combine(Root, "journal");
    public string Desktop => Path.Combine(Root, "desktop");
    public string Spill => Path.Combine(Root, "spill");
    public string TokenFile { get; }
    public string SecretsDir { get; }
    public string ShimSocketDir { get; }
    public string ShimSocket => Path.Combine(ShimSocketDir, "roster.sock");
}
