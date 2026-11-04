using System.Text.RegularExpressions;
using Omabot.Cli;
using Omabot.Common;
using Omabot.Tests.Support;
using Xunit;

namespace Omabot.Tests;

public sealed class DocsTests
{
    private static string Bot => Path.Combine(Applied.RepoRoot(), "bot");

    [Fact(DisplayName = "Docs: the CLI reference covers every command the help lists")]
    public async Task CliReferenceCoversEveryCommand()
    {
        using var r = new TempRoster();
        var env = new CliEnv { Paths = r.Paths, AppDir = r.AppDir, Clock = r.Clock, Out = new StringWriter(), Err = new StringWriter() };
        Assert.Equal(0, await new CliApp(env).RunAsync(["help"]));
        var names = env.Out.ToString()!.Split('\n')
            .Select(l => Regex.Match(l, @"^  ([a-z][a-z-]*)\s"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
        Assert.True(names.Count >= 20, "help listed too few commands");
        var reference = File.ReadAllText(Path.Combine(Bot, "docs", "cli.md"));
        var missing = names.Where(n => !reference.Contains($"\n### {n}\n", StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "Missing from docs/cli.md: " + string.Join(", ", missing));
    }

    [Fact(DisplayName = "Docs: no key, token or password in the docs, templates or README")]
    public void SecretScan()
    {
        var files = Directory.EnumerateFiles(Path.Combine(Bot, "docs"), "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(Bot, "templates"), "*", SearchOption.AllDirectories))
            .Append(Path.Combine(Bot, "README.md"));
        var found = new List<string>();
        foreach (var f in files)
        {
            var lines = File.ReadAllLines(f);
            for (var i = 0; i < lines.Length; i++)
                if (Redactor.ContainsStrongSecret(lines[i])) found.Add($"{Path.GetRelativePath(Bot, f)}:{i + 1}");
        }
        Assert.True(found.Count == 0, "Possible secrets: " + string.Join(", ", found));
    }

    [Fact(DisplayName = "Docs: the team file reference example applies cleanly")]
    public async Task TeamFileExampleApplies()
    {
        var doc = File.ReadAllText(Path.Combine(Bot, "docs", "team-file.md"));
        var example = Regex.Match(doc, @"## Example.*?```yaml\n(.*?)```", RegexOptions.Singleline);
        Assert.True(example.Success);
        using var r = new TempRoster();
        var dir = Path.Combine(r.Root, "example-team");
        Directory.CreateDirectory(Path.Combine(dir, "bots"));
        File.WriteAllText(Path.Combine(dir, "bots", "builder.md"), "You build and test the code. You keep reports short.");
        var file = Path.Combine(dir, "team.yaml");
        File.WriteAllText(file, example.Groups[1].Value);
        var env = new CliEnv { Paths = r.Paths, AppDir = r.AppDir, Clock = r.Clock, Out = new StringWriter(), Err = new StringWriter() };
        Assert.Equal(0, await new CliApp(env).RunAsync(["init"]));
        Assert.Equal(0, await new CliApp(env).RunAsync(["team", "apply", Path.Combine(r.Paths.Home, "team", "team.yaml")]));
        var code = await new CliApp(env).RunAsync(["team", "apply", file, "--dry-run"]);
        Assert.True(code == 0, env.Err.ToString() + env.Out.ToString());
    }
}
