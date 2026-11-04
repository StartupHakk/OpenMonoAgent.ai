// SPDX-License-Identifier: AGPL-3.0-or-later
using Omabot.Common;
using Omabot.Config;
using Omabot.Db;

namespace Omabot.Tests.Support;

/// <summary>A throwaway roster home and work root under /tmp, removed on dispose.</summary>
public sealed class TempRoster : IDisposable
{
    public string Root { get; }
    public RosterPaths Paths { get; }
    public string WorkRoot { get; }
    public string AppDir { get; }
    public RosterConfig Config { get; }
    public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 11, 3, 17, 0, 0, TimeSpan.Zero));

    /// <param name="tenant">When set, the roster is a web-multi user roster with the isolated settings made for that slot.</param>
    public TempRoster(int? tenant = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "omabot-test-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
        Paths = new RosterPaths(Path.Combine(Root, "home"));
        WorkRoot = Path.Combine(Root, "work");
        Directory.CreateDirectory(WorkRoot);
        AppDir = Path.Combine(Root, "app");
        Directory.CreateDirectory(Path.Combine(AppDir, "hooks"));
        SecureFile.EnsureDir(Paths.Home);
        var host = new RosterConfig { WorkspaceRoots = [WorkRoot] };
        host.Gateway.Upstream = "https://relay.example.test/v1";
        host.Images.AcceptedDigest = "sha256:fake";
        Config = host;
        if (tenant is { } t)
        {
            Config = Web.Tenants.ConfigFor(host, Paths, "user" + t.ToString(System.Globalization.CultureInfo.InvariantCulture), t);
            Config.WorkspaceRoots = [WorkRoot];
        }
        Config.Save(Paths);
    }

    public RosterDb Db() => new(Paths.Db);

    public AuditLog Audit(RosterDb db) => new(db, Clock, Redactor.Shared, Paths.Logs);

    /// <summary>Write a team file (and any extra files) into a folder and return its path.</summary>
    public string TeamFile(string yaml, params (string Name, string Text)[] extra)
    {
        var dir = Path.Combine(Root, "team-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        foreach (var (name, text) in extra)
        {
            var p = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }
        var file = Path.Combine(dir, "team.yaml");
        File.WriteAllText(file, yaml);
        return file;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    public const string TwoBots = """
        version: 1
        models:
          flash-next:
            upstream_model: Qwen3.8-Flash-Next
        bots:
          - name: Scout
            title: Finds things
            personality: You are Scout. Curious and short.
            model: flash-next
            tools: { allow: [FileRead, Grep, Glob] }
          - name: Builder
            personality: You are Builder. Careful and calm.
            model: flash-next
            permission_profile: edits
            tools: { allow: [FileRead, FileWrite, FileEdit, Bash] }
            bash: { allow: ["dotnet test*"] }
        """;
}
