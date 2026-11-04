// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Cli;
using Omabot.Common;
using Omabot.Db;
using Omabot.Team;
using Omabot.Tests.Support;

namespace Omabot.Tests;

public sealed class TeamFileTests : IDisposable
{
    private readonly TempRoster _r = new();
    public void Dispose() => _r.Dispose();

    private TeamPlanner Planner(RosterDb db) => new(db, _r.Config, _r.Paths, _r.Clock, _r.Audit(db));

    private static TeamFileException Refused(Action a) => Assert.Throws<TeamFileException>(a);

    [Fact(DisplayName = "UNIT-TEAM-01 valid minimal file parses and the plan lists creates")]
    public void Team01()
    {
        var f = TeamParser.ParseFile(_r.TeamFile(TempRoster.TwoBots));
        Assert.Equal(2, f.Bots.Count);
        var plan = Planner(_r.Db()).Plan(f);
        var text = plan.Render(_r.Clock);
        Assert.Contains("bot \"Scout\": create (flash-next, ask, workspace auto)", text);
        Assert.Contains("bot \"Builder\": create (flash-next, edits, workspace auto)", text);
        Assert.Contains("Plan: 3 to create, 0 to change, 0 unchanged, 0 to delete.", text);
    }

    [Fact(DisplayName = "UNIT-TEAM-02 unknown field is refused with its path (exit 2)")]
    public async Task Team02()
    {
        var ex = Refused(() => TeamParser.ParseFile(_r.TeamFile("version: 1\nbots:\n  - name: A\n    personality: x\n    favorite_color: blue\n")));
        Assert.Contains(ex.Errors, e => e.Path.Contains("favorite_color", StringComparison.Ordinal) && e.Message.Contains("Unknown field", StringComparison.Ordinal));
        var err = new StringWriter();
        var code = await new CliApp(new CliEnv { Paths = _r.Paths, AppDir = _r.AppDir, Out = new StringWriter(), Err = err })
            .RunAsync(["team", "apply", _r.TeamFile("version: 1\nbogus: 1\n")]);
        Assert.Equal(Exit.Invalid, code);
        Assert.Contains("bogus", err.ToString());
    }

    [Fact(DisplayName = "UNIT-TEAM-03 duplicate bot names in any case are refused")]
    public void Team03()
    {
        var f = TeamParser.ParseFile(_r.TeamFile("version: 1\nmodels: { m: { upstream_model: u } }\nbots:\n  - { name: Scout, personality: a, model: m }\n  - { name: SCOUT, personality: b, model: m }\n"));
        var ex = Refused(() => Planner(_r.Db()).Plan(f));
        Assert.Contains(ex.Errors, e => e.Message.Contains("Duplicate", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "UNIT-TEAM-04 rename with was onto an existing name is refused")]
    public void Team04()
    {
        var db = _r.Db();
        var p = Planner(db);
        p.Apply(p.Plan(TeamParser.ParseFile(_r.TeamFile(TempRoster.TwoBots))), "test");
        var yaml = TempRoster.TwoBots.Replace("  - name: Builder\n", "  - name: Builder\n    was: Scout\n", StringComparison.Ordinal);
        var ex = Refused(() => p.Plan(TeamParser.ParseFile(_r.TeamFile(yaml))));
        Assert.Contains(ex.Errors, e => e.Path.Contains("Builder", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "UNIT-TEAM-05 literal secret in a field is refused with its location")]
    public void Team05()
    {
        var ex = Refused(() => TeamParser.ParseFile(_r.TeamFile("version: 1\nbots:\n  - name: A\n    personality: \"use key sk-abcdefghijklmnopqrstuvwxyz0123456789\"\n")));
        var e = Assert.Single(ex.Errors);
        Assert.Contains("personality", e.Path);
        Assert.Equal(4, e.Line);
    }

    [Fact(DisplayName = "UNIT-TEAM-06 personality over 24,000 bytes is refused")]
    public void Team06()
    {
        var big = new string('a', 24_001);
        var ex = Refused(() => TeamParser.ParseFile(_r.TeamFile("version: 1\nbots:\n  - name: A\n    personality: { file: p.md }\n", ("p.md", big))));
        Assert.Contains(ex.Errors, e => e.Message.Contains("24000", StringComparison.Ordinal) || e.Message.Contains("24,000", StringComparison.Ordinal) || e.Message.Contains("bytes", StringComparison.Ordinal));
    }

    [Theory(DisplayName = "UNIT-TEAM-07 workspace with ~ or outside allowed roots is refused")]
    [InlineData("~/code")]
    [InlineData("/etc")]
    public void Team07(string ws)
    {
        var f = TeamParser.ParseFile(_r.TeamFile($"version: 1\nmodels: {{ m: {{ upstream_model: u }} }}\nbots:\n  - {{ name: A, personality: a, model: m, workspace: \"{ws}\" }}\n"));
        Refused(() => Planner(_r.Db()).Plan(f));
    }

    [Fact(DisplayName = "UNIT-TEAM-08 apply twice prints No changes.")]
    public async Task Team08()
    {
        var file = _r.TeamFile(TempRoster.TwoBots);
        var cli = new CliApp(new CliEnv { Paths = _r.Paths, AppDir = _r.AppDir, Clock = _r.Clock, Out = new StringWriter(), Err = new StringWriter() });
        Assert.Equal(0, await cli.RunAsync(["team", "apply", file]));
        var out2 = new StringWriter();
        var cli2 = new CliApp(new CliEnv { Paths = _r.Paths, AppDir = _r.AppDir, Clock = _r.Clock, Out = out2, Err = new StringWriter() });
        Assert.Equal(0, await cli2.RunAsync(["team", "apply", file]));
        Assert.Equal("No changes.\n", out2.ToString());
        Assert.Equal(0, await cli2.RunAsync(["team", "apply", file, "--check"]));
    }

    [Fact(DisplayName = "UNIT-TEAM-09 shared workspace without shared_workspace on both is refused")]
    public void Team09()
    {
        var ws = Path.Combine(_r.WorkRoot, "proj");
        Directory.CreateDirectory(ws);
        var f = TeamParser.ParseFile(_r.TeamFile($"version: 1\nmodels: {{ m: {{ upstream_model: u }} }}\nbots:\n  - {{ name: A, personality: a, model: m, workspace: \"{ws}\", shared_workspace: true }}\n  - {{ name: B, personality: b, model: m, workspace: \"{ws}\" }}\n"));
        Refused(() => Planner(_r.Db()).Plan(f));
    }

    [Fact(DisplayName = "UNIT-TEAM-09 workspace with .. or a symlink is refused")]
    public void Team09b()
    {
        var real = Path.Combine(_r.WorkRoot, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(_r.WorkRoot, "link");
        Directory.CreateSymbolicLink(link, real);
        foreach (var ws in new[] { _r.WorkRoot + "/real/../real", link })
        {
            var f = TeamParser.ParseFile(_r.TeamFile($"version: 1\nmodels: {{ m: {{ upstream_model: u }} }}\nbots:\n  - {{ name: A, personality: a, model: m, workspace: \"{ws}\" }}\n"));
            Refused(() => Planner(_r.Db()).Plan(f));
        }
    }

    [Fact(DisplayName = "UNIT-TEAM-10 secret refusal names field and line and prints no part of the value")]
    public void Team10()
    {
        const string secret = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef0123";
        var ex = Refused(() => TeamParser.ParseFile(_r.TeamFile($"version: 1\nbots:\n  - name: A\n    personality: ok\n    instructions: \"token is {secret}\"\n")));
        var text = ex.Message + string.Join("\n", ex.Errors);
        Assert.Contains("instructions", text);
        Assert.Contains("line 5", text);
        foreach (var piece in new[] { secret, secret[..8], secret[^8..], "ABCDEFGH" }) Assert.DoesNotContain(piece, text);
    }

    [Fact(DisplayName = "REG-04 apply, export, apply again prints No changes.")]
    public async Task Reg04()
    {
        var o = new StringWriter();
        var env = new CliEnv { Paths = _r.Paths, AppDir = _r.AppDir, Clock = _r.Clock, Out = o, Err = new StringWriter() };
        var defaultTeam = _r.TeamFile(Render.Templates.Get("team.default.yaml"),
            ("bots/head-honcho.md", Render.Templates.Get("bots/head-honcho.md")), ("bots/health.md", Render.Templates.Get("bots/health.md")));
        Assert.Equal(0, await new CliApp(env).RunAsync(["team", "apply", defaultTeam]));
        Assert.Equal(0, await new CliApp(env).RunAsync(["team", "apply", _r.TeamFile(TempRoster.TwoBots)]));
        var exported = Path.Combine(_r.Root, "export.yaml");
        Assert.Equal(0, await new CliApp(env).RunAsync(["team", "export", "--out", exported]));
        var o2 = new StringWriter();
        var env2 = new CliEnv { Paths = _r.Paths, AppDir = _r.AppDir, Clock = _r.Clock, Out = o2, Err = new StringWriter() };
        Assert.Equal(0, await new CliApp(env2).RunAsync(["team", "apply", exported]));
        Assert.Equal("No changes.\n", o2.ToString());
    }

    [Fact(DisplayName = "Apply never deletes: a bot missing from the file is reported as not in file")]
    public void ApplyNeverDeletes()
    {
        var db = _r.Db();
        var p = Planner(db);
        p.Apply(p.Plan(TeamParser.ParseFile(_r.TeamFile(TempRoster.TwoBots))), "test");
        var only = "version: 1\nmodels: { flash-next: { upstream_model: Qwen3.8-Flash-Next } }\nbots:\n  - { name: Scout, title: Finds things, personality: You are Scout. Curious and short., model: flash-next, tools: { allow: [FileRead, Grep, Glob] } }\n";
        var plan = p.Plan(TeamParser.ParseFile(_r.TeamFile(only)));
        Assert.Contains("bot \"Builder\": not in file (left alone)", plan.Render(_r.Clock));
        p.Apply(plan, "test");
        Assert.Equal(2, new BotStore(db, _r.Clock).All().Count);
    }

    [Fact(DisplayName = "Each applied change writes exactly one audit row")]
    public void OneAuditRowPerChange()
    {
        var db = _r.Db();
        var p = Planner(db);
        var before = (long)db.Scalar("SELECT COUNT(*) FROM audit;")!;
        var plan = p.Plan(TeamParser.ParseFile(_r.TeamFile(TempRoster.TwoBots)));
        var result = p.Apply(plan, "test");
        Assert.True(result.Ok);
        var after = (long)db.Scalar("SELECT COUNT(*) FROM audit;")!;
        Assert.Equal(plan.Creates + plan.Updates, after - before);
        Assert.Null(_r.Audit(db).Verify());
    }

    [Fact(DisplayName = "Update plan shows changed lines and tool changes")]
    public void UpdatePlanFormat()
    {
        var db = _r.Db();
        var p = Planner(db);
        p.Apply(p.Plan(TeamParser.ParseFile(_r.TeamFile(TempRoster.TwoBots))), "test");
        var changed = TempRoster.TwoBots
            .Replace("You are Builder. Careful and calm.", "\"You are Builder.\\nCareful.\\nCalm.\"", StringComparison.Ordinal)
            .Replace("[FileRead, FileWrite, FileEdit, Bash]", "[FileRead, FileWrite, FileEdit, Bash, Grep]", StringComparison.Ordinal);
        var text = p.Plan(TeamParser.ParseFile(_r.TeamFile(changed))).Render(_r.Clock);
        Assert.Contains("bot \"Builder\": update personality (3 lines changed); tools +Grep", text);
        Assert.Contains("Plan: 0 to create, 1 to change", text);
    }

    [Fact(DisplayName = "Health role with a write tool is refused")]
    public void HealthRoleReadOnly()
    {
        var f = TeamParser.ParseFile(_r.TeamFile("version: 1\nmodels: { m: { upstream_model: u } }\nbots:\n  - { name: health, role: health, personality: a, model: m, tools: { allow: [FileWrite] } }\n"));
        Refused(() => Planner(_r.Db()).Plan(f));
    }

    [Fact(DisplayName = "External posting tools are refused")]
    public void ExternalPostRefused()
    {
        var f = TeamParser.ParseFile(_r.TeamFile("version: 1\nmodels: { m: { upstream_model: u } }\nbots:\n  - { name: A, personality: a, model: m, tools: { allow: [mcp__x__create_post] } }\n"));
        Refused(() => Planner(_r.Db()).Plan(f));
    }

    [Fact(DisplayName = "Default team file parses with head-honcho as lead and a separate health bot")]
    public void DefaultTeam()
    {
        var file = _r.TeamFile(Render.Templates.Get("team.default.yaml"),
            ("bots/head-honcho.md", Render.Templates.Get("bots/head-honcho.md")), ("bots/health.md", Render.Templates.Get("bots/health.md")));
        var resolved = new TeamResolver(_r.Config, _r.Paths).Resolve(TeamParser.ParseFile(file), new LiveState([], []));
        Assert.Equal("lead", resolved.Bots.Single(b => b.Spec.Name == "head-honcho").Spec.Role);
        var health = resolved.Bots.Single(b => b.Spec.Name == "health").Spec;
        Assert.Equal("health", health.Role);
        Assert.All(health.ToolsAllow, t => Assert.True(ToolCatalog.IsReadOnly(t), t));
        Assert.Single(health.Routines);
    }

    [Fact(DisplayName = "Cron validation uses five fields")]
    public void Cron()
    {
        Assert.Null(Routines.CronRules.Validate("0 7 * * MON-FRI"));
        Assert.NotNull(Routines.CronRules.Validate("0 0 7 * * MON"));
        Assert.NotNull(Routines.CronRules.Validate("nonsense"));
        var next = Routines.CronRules.Next("0 7 * * *", "America/Phoenix", new DateTimeOffset(2026, 11, 3, 15, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 11, 4, 14, 0, 0, TimeSpan.Zero), next);
    }
}
