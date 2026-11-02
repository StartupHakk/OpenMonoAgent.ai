using FluentAssertions;
using OpenMono.Config;

namespace OpenMono.Tests.Config;

public class GitConfigTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _emptyDataDir;
    private readonly Dictionary<string, string?> _savedEnv = [];

    private static readonly string[] EnvVars =
    [
        "OPENMONO_DATA_DIR", "OPENMONO_GIT_PROFILE",
    ];

    public GitConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-git-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _emptyDataDir = Path.Combine(_tempDir, "isolated-data");
        Directory.CreateDirectory(_emptyDataDir);

        foreach (var name in EnvVars)
        {
            _savedEnv[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
        Environment.SetEnvironmentVariable("OPENMONO_DATA_DIR", _emptyDataDir);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _savedEnv)
            Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private void WriteSettings(string json)
        => File.WriteAllText(Path.Combine(_emptyDataDir, "settings.json"), json);

    [Fact]
    public void Load_WithNoGitSection_ReturnsEmptyGitConfig()
    {
        var config = ConfigLoader.Load(_tempDir);
        config.Git.Profiles.Should().BeEmpty();
        config.Git.Default.Should().BeNull();
        var (profile, error) = config.Git.ResolveSelectedProfile();
        profile.Should().BeNull();
        error.Should().BeNull();
    }

    [Fact]
    public void Load_MergesGitSectionFromSettingsFile()
    {
        WriteSettings("""
        {
          "git": {
            "default": "work",
            "profiles": {
              "work": {
                "type": "ssh",
                "ssh_key": "~/.ssh/id_ed25519_work",
                "user": "Jane",
                "email": "jane@work.dev"
              }
            }
          }
        }
        """);

        var config = ConfigLoader.Load(_tempDir);
        config.Git.Default.Should().Be("work");
        config.Git.Profiles.Should().ContainKey("work");
        config.Git.Profiles["work"].Type.Should().Be("ssh");
        config.Git.Profiles["work"].SshKey.Should().Be("~/.ssh/id_ed25519_work");

        var (profile, error) = config.Git.ResolveSelectedProfile();
        error.Should().BeNull();
        profile.Should().NotBeNull();
        profile!.Type.Should().Be("ssh");
    }

    [Fact]
    public void MergeFrom_AddsProfilesAndFillsPartialFields()
    {
        var config = new GitConfig();
        config.Profiles["work"] = new GitProfile { Type = "https" };

        var overrides = new GitConfig
        {
            Default = "work",
            Profiles =
            {
                ["work"] = new GitProfile { User = "Jane" },
                ["other"] = new GitProfile { Type = "ssh" }
            }
        };
        config.MergeFrom(overrides);

        config.Default.Should().Be("work");
        config.Profiles.Should().HaveCount(2);
        config.Profiles["work"].Type.Should().Be("https"); // existing kept
        config.Profiles["work"].User.Should().Be("Jane");  // filled from override
        config.Profiles["other"].Type.Should().Be("ssh");
    }

    [Fact]
    public void Resolve_SessionProfileOverridesDefault()
    {
        var config = new GitConfig
        {
            Default = "work",
            SessionProfile = "personal",
            Profiles =
            {
                ["work"] = new GitProfile { Type = "ssh" },
                ["personal"] = new GitProfile { Type = "https" }
            }
        };

        var (profile, error) = config.ResolveSelectedProfile();
        error.Should().BeNull();
        profile!.Type.Should().Be("https");
    }

    [Fact]
    public void Resolve_NoSelection_ReturnsNoProfileNoError()
    {
        var config = new GitConfig
        {
            Profiles = { ["work"] = new GitProfile { Type = "ssh" } }
        };

        var (profile, error) = config.ResolveSelectedProfile();
        profile.Should().BeNull();
        error.Should().BeNull();
    }

    [Fact]
    public void Resolve_UnknownProfile_ReturnsError()
    {
        var config = new GitConfig
        {
            SessionProfile = "nope",
            Profiles = { ["work"] = new GitProfile { Type = "ssh" } }
        };

        var (profile, error) = config.ResolveSelectedProfile();
        profile.Should().BeNull();
        error.Should().NotBeNull().And.Contain("nope").And.Contain("work");
    }

    [Fact]
    public void Resolve_InvalidType_ReturnsError()
    {
        var config = new GitConfig
        {
            SessionProfile = "bad",
            Profiles = { ["bad"] = new GitProfile { Type = "carrier-pigeon" } }
        };

        var (profile, error) = config.ResolveSelectedProfile();
        profile.Should().BeNull();
        error.Should().NotBeNull().And.Contain("carrier-pigeon");
    }

    [Fact]
    public void Load_EnvVarSelectsProfile_SessionProfileWins()
    {
        WriteSettings("""
        { "git": { "default": "work", "profiles": {
            "work": { "type": "ssh" }, "personal": { "type": "https" } } } }
        """);
        Environment.SetEnvironmentVariable("OPENMONO_GIT_PROFILE", "personal");

        var config = ConfigLoader.Load(_tempDir);
        config.Git.SessionProfile.Should().Be("personal");

        var (profile, error) = config.Git.ResolveSelectedProfile();
        error.Should().BeNull();
        profile!.Type.Should().Be("https");
    }
}
