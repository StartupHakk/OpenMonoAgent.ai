using FluentAssertions;
using OpenMono.Cli.Git;
using OpenMono.Config;
using OpenMono.Session;
using Xunit;

namespace OpenMono.Tests.Git;

public class GitProfileSelectionTests
{
    private static GitConfig Git(string? env, string? def) => new()
    {
        SessionProfile = env,
        Default = def,
        Profiles =
        {
            ["work"] = new GitProfile { Type = "ssh" },
            ["personal"] = new GitProfile { Type = "https", Token = "x" }
        }
    };

    private static SessionState? Session(string? name, bool touched)
    {
        if (!touched) return null;
        var s = new SessionState();
        s.Meta.GitProfileName = name;
        s.Meta.GitProfileTouched = true;
        return s;
    }

    [Fact]
    public void Touched_ValidProfile_Wins()
    {
        var (name, warning) = GitProfileSelection.Resolve(Git("env", "work"), Session("personal", true));
        name.Should().Be("personal");
        warning.Should().BeNull();
    }

    [Fact]
    public void Touched_Off_Wins_Over_Env_And_Default()
    {
        // Explicit /git off: must NOT fall back to the env var or git.default.
        var (name, warning) = GitProfileSelection.Resolve(Git("personal", "work"), Session(null, true));
        name.Should().BeNull();
        warning.Should().BeNull();
    }

    [Fact]
    public void Touched_StaleProfile_Warns_and_Passthrough()
    {
        // A stale persisted name degrades to host pass-through — it must NOT
        // silently switch to another profile (the wrong token could get pushed).
        var (name, warning) = GitProfileSelection.Resolve(Git("personal", "work"), Session("gone", true));
        name.Should().BeNull();
        warning.Should().Contain("'gone'");
    }

    [Fact]
    public void Touched_StaleProfile_NoFallback_Available()
    {
        var (name, warning) = GitProfileSelection.Resolve(Git(null, null), Session("gone", true));
        name.Should().BeNull();
        warning.Should().Contain("'gone'");
    }

    [Fact]
    public void Untouched_EnvVar_Wins_Over_Default()
    {
        var (name, warning) = GitProfileSelection.Resolve(Git("personal", "work"), Session("work", false));
        name.Should().Be("personal");
        warning.Should().BeNull();
    }

    [Fact]
    public void Untouched_NoEnv_UsesDefault()
    {
        var (name, warning) = GitProfileSelection.Resolve(Git(null, "work"), Session(null, false));
        name.Should().Be("work");
        warning.Should().BeNull();
    }

    [Fact]
    public void Untouched_NothingConfigured_HostPassthrough()
    {
        var (name, warning) = GitProfileSelection.Resolve(Git(null, null), Session(null, false));
        name.Should().BeNull();
        warning.Should().BeNull();
    }

    [Fact]
    public void Untouched_UnknownEnvName_Warns_and_Passthrough()
    {
        var (name, warning) = GitProfileSelection.Resolve(Git("ghost", null), Session(null, false));
        name.Should().BeNull();
        warning.Should().Contain("ghost");
    }

    [Fact]
    public void Untouched_InvalidTypeProfile_Warns()
    {
        var git = Git("bad", null);
        git.Profiles["bad"] = new GitProfile { Type = "carrier-pigeon" };
        var (name, warning) = GitProfileSelection.Resolve(git, Session(null, false));
        name.Should().BeNull();
        warning.Should().Contain("invalid type");
    }
}
