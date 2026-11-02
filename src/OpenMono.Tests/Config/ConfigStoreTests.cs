using System.Text.Json;
using FluentAssertions;
using OpenMono.Config;
using Xunit;

namespace OpenMono.Tests.Config;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _dir;

    public ConfigStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"openmono-configstore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static AppConfig Cfg(string dataDir) => new() { DataDirectory = dataDir };

    [Fact]
    public void Upsert_CreatesFileWithGitSection()
    {
        var cfg = Cfg(_dir);
        ConfigStore.UpsertGitProfile(cfg, "work",
            new GitProfile { Type = "ssh", SshKey = "~/.ssh/id_ed25519", User = "Jane", Email = "j@e.com" });

        var path = ConfigStore.UserSettingsPath(cfg);
        File.Exists(path).Should().BeTrue();

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var p = doc.RootElement.GetProperty("git").GetProperty("profiles").GetProperty("work");
        p.GetProperty("type").GetString().Should().Be("ssh");
        p.GetProperty("ssh_key").GetString().Should().Be("~/.ssh/id_ed25519");
        p.GetProperty("user").GetString().Should().Be("Jane");
        p.GetProperty("email").GetString().Should().Be("j@e.com");
    }

    [Fact]
    public void Upsert_PreservesOtherSections()
    {
        var cfg = Cfg(_dir);
        File.WriteAllText(ConfigStore.UserSettingsPath(cfg), """
        {
          "llm": { "endpoint": "http://localhost:7474", "model": "qwen3" },
          "web": { "gateway": "http://gw" },
          "mcp_servers": { "github": { "command": "npx", "args": ["-y", "gh-mcp"] } }
        }
        """);

        ConfigStore.UpsertGitProfile(cfg, "work",
            new GitProfile { Type = "https", Host = "github.com", TokenEnv = "GITHUB_PAT" });

        using var doc = JsonDocument.Parse(File.ReadAllText(ConfigStore.UserSettingsPath(cfg)));
        doc.RootElement.GetProperty("llm").GetProperty("endpoint").GetString().Should().Be("http://localhost:7474");
        doc.RootElement.GetProperty("llm").GetProperty("model").GetString().Should().Be("qwen3");
        doc.RootElement.GetProperty("web").GetProperty("gateway").GetString().Should().Be("http://gw");
        doc.RootElement.GetProperty("mcp_servers").GetProperty("github").GetProperty("command").GetString().Should().Be("npx");
        doc.RootElement.GetProperty("git").GetProperty("profiles").GetProperty("work").GetProperty("token_env").GetString()
            .Should().Be("GITHUB_PAT");
    }

    [Fact]
    public void Upsert_KeepsExistingGitDefault()
    {
        var cfg = Cfg(_dir);
        File.WriteAllText(ConfigStore.UserSettingsPath(cfg), """
        { "git": { "default": "home", "profiles": { "home": { "type": "ssh", "ssh_key": "~/.ssh/id_ed" } } } }
        """);

        ConfigStore.UpsertGitProfile(cfg, "work", new GitProfile { Type = "https", Host = "github.com" });

        using var doc = JsonDocument.Parse(File.ReadAllText(ConfigStore.UserSettingsPath(cfg)));
        doc.RootElement.GetProperty("git").GetProperty("default").GetString().Should().Be("home");
        doc.RootElement.GetProperty("git").GetProperty("profiles").GetProperty("home")
            .GetProperty("ssh_key").GetString().Should().Be("~/.ssh/id_ed");
        doc.RootElement.GetProperty("git").GetProperty("profiles").GetProperty("work")
            .GetProperty("host").GetString().Should().Be("github.com");
    }

    [Fact]
    public void Upsert_ReplacesProfileWithSameName()
    {
        var cfg = Cfg(_dir);
        ConfigStore.UpsertGitProfile(cfg, "work",
            new GitProfile { Type = "ssh", SshKey = "~/.ssh/old" });
        ConfigStore.UpsertGitProfile(cfg, "work",
            new GitProfile { Type = "https", Host = "gitlab.example.com" });

        using var doc = JsonDocument.Parse(File.ReadAllText(ConfigStore.UserSettingsPath(cfg)));
        var profiles = doc.RootElement.GetProperty("git").GetProperty("profiles");
        profiles.EnumerateObject().Should().ContainSingle().Which.Name.Should().Be("work");
        profiles.GetProperty("work").GetProperty("type").GetString().Should().Be("https");
        profiles.GetProperty("work").TryGetProperty("ssh_key", out _).Should().BeFalse();
    }

    [Fact]
    public void Upsert_TokenOmittedWhenNull()
    {
        var cfg = Cfg(_dir);
        ConfigStore.UpsertGitProfile(cfg, "work",
            new GitProfile { Type = "https", Host = "github.com", TokenEnv = "GITHUB_PAT" });

        using var doc = JsonDocument.Parse(File.ReadAllText(ConfigStore.UserSettingsPath(cfg)));
        doc.RootElement.GetProperty("git").GetProperty("profiles").GetProperty("work")
            .TryGetProperty("token", out _).Should().BeFalse();
    }

    [Fact]
    public void Upsert_UnknownExtraKeys_ArePreserved()
    {
        // A future/foreign key this build doesn't model must survive the round trip.
        var cfg = Cfg(_dir);
        File.WriteAllText(ConfigStore.UserSettingsPath(cfg), """
        { "llm": { "model": "qwen3" }, "totally_unknown_section": { "a": 1 } }
        """);

        ConfigStore.UpsertGitProfile(cfg, "work", new GitProfile { Type = "https" });

        using var doc = JsonDocument.Parse(File.ReadAllText(ConfigStore.UserSettingsPath(cfg)));
        doc.RootElement.GetProperty("totally_unknown_section").GetProperty("a").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("llm").GetProperty("model").GetString().Should().Be("qwen3");
    }

    [Fact]
    public void Upsert_NewFile_IsLockedDownOnUnix()
    {
        if (OperatingSystem.IsWindows()) return;
        var cfg = Cfg(_dir);
        ConfigStore.UpsertGitProfile(cfg, "work", new GitProfile { Type = "https", Token = "ghp_x" });

        var mode = File.GetUnixFileMode(ConfigStore.UserSettingsPath(cfg));
        mode.HasFlag(UnixFileMode.OtherRead).Should().BeFalse();
        mode.HasFlag(UnixFileMode.GroupRead).Should().BeFalse();
        mode.HasFlag(UnixFileMode.UserRead).Should().BeTrue();
    }

    [Fact]
    public void Upsert_PrefersExistingFileMode_Untouched()
    {
        if (OperatingSystem.IsWindows()) return;
        var cfg = Cfg(_dir);
        var path = ConfigStore.UserSettingsPath(cfg);
        File.WriteAllText(path, "{}");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead); // deliberately looser

        ConfigStore.UpsertGitProfile(cfg, "work", new GitProfile { Type = "https" });

        var mode = File.GetUnixFileMode(path);
        mode.HasFlag(UnixFileMode.GroupRead).Should().BeTrue("a pre-existing file's mode must not be tightened or loosened");
    }
}
