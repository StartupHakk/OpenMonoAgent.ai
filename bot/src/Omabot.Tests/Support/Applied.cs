// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Cli;
using Omabot.Db;
using Omabot.Render;

namespace Omabot.Tests.Support;

public static class Applied
{
    /// <summary>Apply the default team (head-honcho and health) plus Scout and Builder through the CLI.</summary>
    public static async Task<RosterDb> DefaultPlusTwoAsync(TempRoster r)
    {
        var defaultTeam = r.TeamFile(Templates.Get("team.default.yaml"),
            ("bots/head-honcho.md", Templates.Get("bots/head-honcho.md")), ("bots/health.md", Templates.Get("bots/health.md")));
        var env = new CliEnv { Paths = r.Paths, AppDir = r.AppDir, Clock = r.Clock, Out = new StringWriter(), Err = new StringWriter() };
        var err = (StringWriter)env.Err;
        if (await new CliApp(env).RunAsync(["team", "apply", defaultTeam]) != 0) throw new InvalidOperationException(err.ToString());
        if (await new CliApp(env).RunAsync(["team", "apply", r.TeamFile(TempRoster.TwoBots)]) != 0) throw new InvalidOperationException(err.ToString());
        return r.Db();
    }

    public static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !(Directory.Exists(Path.Combine(d.FullName, "bot")) && Directory.Exists(Path.Combine(d.FullName, ".git")))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
