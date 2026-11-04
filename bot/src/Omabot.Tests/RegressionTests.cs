// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using Omabot.Cli;
using Omabot.Tests.Support;

namespace Omabot.Tests;

public sealed class RegressionTests : IDisposable
{
    private readonly TempRoster _r = new();
    public void Dispose() => _r.Dispose();

    private static string Git(string repo, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return o;
    }

    [Fact(DisplayName = "REG-02 the OMA checkout outside bot/ stays clean")]
    public void Reg02()
    {
        var root = Applied.RepoRoot();
        var dirty = Git(root, "status", "--porcelain", "--", ".", ":(exclude)bot")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(dirty.Length == 0, "Changed outside bot/: " + string.Join(", ", dirty));
    }

    [Fact(DisplayName = "REG-06 no em dash or en dash in any roster file")]
    public void Reg06()
    {
        var bot = Path.Combine(Applied.RepoRoot(), "bot");
        var bad = new List<string>();
        foreach (var f in Directory.EnumerateFiles(bot, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(bot, f);
            if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal) || rel.StartsWith("bin/", StringComparison.Ordinal) || rel.StartsWith("obj/", StringComparison.Ordinal)) continue;
            if (new FileInfo(f).Length > 5_000_000) continue;
            var text = File.ReadAllText(f);
            if (text.Contains('\u2014') || text.Contains('\u2013')) bad.Add(rel);
        }
        Assert.True(bad.Count == 0, "Dashes found in: " + string.Join(", ", bad));
    }

    [Fact(DisplayName = "REG-01 OMA's own test suite passes unchanged", Skip = "Run by CI on the OMA solution; the roster build never edits OMA files (REG-02 proves it).")]
    public void Reg01() { }

    [Fact(DisplayName = "SCN-01 two bots introduce themselves in their own voices", Skip = "Needs Docker, the openmono-agent image and a reachable inference server through the relay.")]
    public void Scn01() { }

    [Fact(DisplayName = "INT-02 real agent image with a fake model server", Skip = "Needs Docker and the openmono-agent image.")]
    public void Int02() { }

    [Fact(DisplayName = "INT-05 hooks mounted read only run inside the container", Skip = "Needs Docker and the openmono-agent image.")]
    public void Int05() { }

    [Fact(DisplayName = "CLI: init writes the default team, apply renders, status lists, doctor runs offline")]
    public async Task CliFlow()
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var env = new CliEnv { Paths = _r.Paths, AppDir = _r.AppDir, Clock = _r.Clock, Out = o, Err = e };
        Assert.Equal(0, await new CliApp(env).RunAsync(["init"]));
        var team = Path.Combine(_r.Paths.Home, "team", "team.yaml");
        Assert.True(File.Exists(team), e.ToString());
        Assert.Equal(0, await new CliApp(env).RunAsync(["team", "apply", team]));
        Assert.True(File.Exists(_r.Paths.Bot("head-honcho").SystemPrompt));
        Assert.True(File.Exists(_r.Paths.Bot("health").Settings));
        o.GetStringBuilder().Clear();
        Assert.Equal(0, await new CliApp(env).RunAsync(["status"]));
        Assert.Contains("head-honcho", o.ToString());
        Assert.Contains("health", o.ToString());
        o.GetStringBuilder().Clear();
        var code = await new CliApp(env).RunAsync(["doctor", "--json"]);
        Assert.Contains("audit_chain", o.ToString());
        Assert.InRange(code, 0, 1);
        Assert.Equal(Exit.Invalid, await new CliApp(env).RunAsync(["team", "apply", Path.Combine(_r.Root, "missing.yaml")]));
        Assert.Equal(0, await new CliApp(env).RunAsync(["version"]));
    }

    [Fact(DisplayName = "CLI: --check exits 6 when the file would change things")]
    public async Task CheckExit()
    {
        var env = new CliEnv { Paths = _r.Paths, AppDir = _r.AppDir, Clock = _r.Clock, Out = new StringWriter(), Err = new StringWriter() };
        Assert.Equal(6, await new CliApp(env).RunAsync(["team", "apply", _r.TeamFile(TempRoster.TwoBots), "--check"]));
    }
}
