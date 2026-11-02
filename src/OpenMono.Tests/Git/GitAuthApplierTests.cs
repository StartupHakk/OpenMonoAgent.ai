using System.Diagnostics;
using FluentAssertions;
using OpenMono.Cli.Git;
using OpenMono.Config;

namespace OpenMono.Tests.Git;

public class GitAuthApplierTests : IDisposable
{
    private readonly string _tempHome;
    private readonly Dictionary<string, string?> _savedEnv = [];

    private static readonly string[] EnvVars =
    [
        "GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0", "GIT_CONFIG_KEY_1", "GIT_CONFIG_VALUE_1",
        "GIT_SSH_COMMAND", "GH_PAT_TEST", "HOME",
    ];

    public GitAuthApplierTests()
    {
        _tempHome = Path.Combine(Path.GetTempPath(), $"openmono-git-applier-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempHome, ".ssh"));

        foreach (var name in EnvVars)
        {
            _savedEnv[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
        // PathGuard and friends resolve ~/.ssh from the user profile; point it at
        // our temp home so the ssh-key containment check is deterministic.
        Environment.SetEnvironmentVariable("HOME", _tempHome);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _savedEnv)
            Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_tempHome, recursive: true); } catch { }
    }

    private static GitConfig Config(GitProfile profile, string name = "work", string? session = null)
        => new()
        {
            Default = name,
            SessionProfile = session,
            Profiles = { [name] = profile }
        };

    [Fact]
    public void NoProfile_ReturnsEmptyDict()
    {
        GitAuthApplier.ResolveEnvVars(new GitConfig())
            .Should().BeEmpty();
    }

    [Fact]
    public void UnknownProfile_Throws()
    {
        var act = () => GitAuthApplier.ResolveEnvVars(
            Config(new GitProfile { Type = "ssh" }, session: "ghost"));
        act.Should().Throw<ArgumentException>().WithMessage("*ghost*");
    }

    [Fact]
    public void HttpsProfile_ProducesCredentialConfigEntries()
    {
        var vars = GitAuthApplier.ResolveEnvVars(Config(new GitProfile
        {
            Type = "https",
            User = "octocat",
            Token = "ghp_secret",
            Email = "jane@example.com",
        }));

        // credential.helper + username + password + user.name + user.email = 5
        vars["GIT_CONFIG_COUNT"].Should().Be("5");
        vars.Values.Should().Contain("credential.helper");
        vars.Values.Should().Contain("!true");
        vars.Values.Should().Contain("credential.https://github.com.username");
        vars.Values.Should().Contain("credential.https://github.com.password");
        vars.Values.Should().Contain("ghp_secret");
        vars.Values.Should().Contain("octocat");
        vars.Values.Should().Contain("user.email");
        vars.Values.Should().Contain("jane@example.com");
        vars.Should().NotContainKey("GIT_SSH_COMMAND");
    }

    [Fact]
    public void HttpsProfile_CustomHost_UsedInCredentialKeys()
    {
        var vars = GitAuthApplier.ResolveEnvVars(Config(new GitProfile
        {
            Type = "https",
            Host = "gitlab.example.com",
            Token = "glpat_secret",
        }));

        vars.Values.Should().Contain("credential.https://gitlab.example.com.password");
        vars.Values.Should().NotContain("credential.https://github.com.password");
    }

    [Fact]
    public void HttpsProfile_TokenEnv_ReadsFromEnvironment()
    {
        Environment.SetEnvironmentVariable("GH_PAT_TEST", "env-token");
        var vars = GitAuthApplier.ResolveEnvVars(Config(new GitProfile
        {
            Type = "https",
            TokenEnv = "GH_PAT_TEST",
        }));

        vars.Values.Should().Contain("env-token");
    }

    [Fact]
    public void HttpsProfile_TokenEnvUnset_Throws()
    {
        var act = () => GitAuthApplier.ResolveEnvVars(Config(new GitProfile
        {
            Type = "https",
            TokenEnv = "GH_PAT_TEST",
        }));
        act.Should().Throw<ArgumentException>().WithMessage("*GH_PAT_TEST*");
    }

    [Fact]
    public void HttpsProfile_NoTokenAtAll_Throws()
    {
        var act = () => GitAuthApplier.ResolveEnvVars(Config(new GitProfile { Type = "https" }));
        act.Should().Throw<ArgumentException>().WithMessage("*token*");
    }

    [Fact]
    public void SshProfile_OverridesGitSshCommand()
    {
        var vars = GitAuthApplier.ResolveEnvVars(Config(new GitProfile
        {
            Type = "ssh",
            SshKey = "~/.ssh/id_ed25519_work",
        }));

        vars.Should().ContainKey("GIT_SSH_COMMAND");
        vars["GIT_SSH_COMMAND"].Should()
            .Be($"ssh -i {Path.Combine(_tempHome, ".ssh", "id_ed25519_work")} " +
                "-o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=accept-new");
        vars["GIT_CONFIG_COUNT"].Should().Be("0");
    }

    [Fact]
    public void SshProfile_IdentityVars_Emitted()
    {
        var vars = GitAuthApplier.ResolveEnvVars(Config(new GitProfile
        {
            Type = "ssh",
            SshKey = "~/.ssh/id_ed25519_work",
            User = "Jane",
            Email = "jane@work.dev",
        }));

        vars["GIT_CONFIG_COUNT"].Should().Be("2");
        vars.Values.Should().Contain("user.name").And.Contain("Jane");
        vars.Values.Should().Contain("user.email").And.Contain("jane@work.dev");
    }

    [Fact]
    public void SshProfile_KeyOutsideSshDir_Throws()
    {
        var act = () => GitAuthApplier.ResolveEnvVars(Config(new GitProfile
        {
            Type = "ssh",
            SshKey = "/etc/secrets/key",
        }));
        act.Should().Throw<ArgumentException>().WithMessage("*~/.ssh*");
    }

    [Fact]
    public void SshProfile_MissingKey_Throws()
    {
        var act = () => GitAuthApplier.ResolveEnvVars(Config(new GitProfile { Type = "ssh" }));
        act.Should().Throw<ArgumentException>().WithMessage("*ssh_key*");
    }

    [Fact]
    public void InheritedLauncherConfig_IsAppendedNotClobbered()
    {
        // The container launcher exports safe.directory before the agent starts.
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "safe.directory");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "/workspace");

        var vars = GitAuthApplier.ResolveEnvVars(Config(new GitProfile
        {
            Type = "https",
            Token = "ghp_secret",
        }));

        // 1 inherited + 3 profile (no identity on this profile) = 4
        vars["GIT_CONFIG_COUNT"].Should().Be("4");
        // The inherited triplet survives as entry 0.
        vars["GIT_CONFIG_KEY_0"].Should().Be("safe.directory");
        vars["GIT_CONFIG_VALUE_0"].Should().Be("/workspace");
        vars.Values.Should().Contain("credential.helper").And.Contain("!true");
    }

    [Fact]
    public void InheritedCredentialHelper_IsDropped()
    {
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "2");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "safe.directory");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "/workspace");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_1", "credential.helper");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_1", "store");

        var vars = GitAuthApplier.ResolveEnvVars(Config(new GitProfile
        {
            Type = "https",
            Token = "ghp_secret",
        }));

        // 2 inherited - 1 dropped + 3 profile (no identity) = 4
        vars["GIT_CONFIG_COUNT"].Should().Be("4");
        vars.Values.Should().NotContain("store");
        vars.Values.Should().Contain("!true");
    }

    [Fact]
    public void ResolveEnvVarsFor_NullName_ReturnsEmpty()
    {
        GitAuthApplier.ResolveEnvVarsFor(null, Config(new GitProfile { Type = "https", Token = "t" }))
            .Should().BeEmpty();
    }

    [Fact]
    public void ResolveEnvVarsFor_UnknownName_ReturnsEmpty()
    {
        // Per-child path: a name that is not a configured profile degrades to a
        // silent no-op (host pass-through) rather than throwing.
        GitAuthApplier.ResolveEnvVarsFor("ghost", Config(new GitProfile { Type = "https", Token = "t" }))
            .Should().BeEmpty();
    }

    [Fact]
    public void ResolveEnvVarsFor_InvalidType_ReturnsEmpty()
    {
        var config = Config(new GitProfile { Type = "carrier-pigeon" });
        GitAuthApplier.ResolveEnvVarsFor("work", config).Should().BeEmpty();
    }

    [Fact]
    public void ResolveEnvVarsFor_ValidName_MatchesBuildEnvVars()
    {
        var profile = new GitProfile { Type = "https", User = "octocat", Token = "ghp_secret" };
        GitAuthApplier.ResolveEnvVarsFor("work", Config(profile))
            .Should().BeEquivalentTo(GitAuthApplier.BuildEnvVars(profile));
    }

    [Fact]
    public void ApplyTo_LayersGitVarsOntoStartInfo()
    {
        var psi = new ProcessStartInfo { FileName = "/bin/bash" };
        GitAuthApplier.ApplyTo(psi, "work", Config(new GitProfile
        {
            Type = "https",
            Token = "ghp_secret",
        }));

        psi.Environment["GIT_CONFIG_COUNT"].Should().Be("3");
        psi.Environment.Should().ContainValues("credential.helper", "!true");
    }

    [Fact]
    public void ApplyTo_NullName_IsNoOp()
    {
        var psi = new ProcessStartInfo { FileName = "/bin/bash" };
        GitAuthApplier.ApplyTo(psi, null, Config(new GitProfile { Type = "https", Token = "t" }));
        psi.Environment.Should().NotContainKey("GIT_CONFIG_COUNT");
    }

    [Fact]
    public void ApplyTo_UnknownName_IsNoOp()
    {
        var psi = new ProcessStartInfo { FileName = "/bin/bash" };
        GitAuthApplier.ApplyTo(psi, "ghost", Config(new GitProfile { Type = "https", Token = "t" }));
        psi.Environment.Should().NotContainKey("GIT_CONFIG_COUNT");
    }

    [Fact]
    public void ApplyTo_WritesOnlyToStartInfo_NotProcessEnv()
    {
        // ApplyTo layers onto the child's ProcessStartInfo and must NOT mutate the
        // agent process environment (unlike the old startup-only Apply): other
        // sessions in the same process must keep their own git identity.
        var psi = new ProcessStartInfo { FileName = "/bin/bash" };
        GitAuthApplier.ApplyTo(psi, "work", Config(new GitProfile { Type = "https", Token = "t" }));

        psi.Environment["GIT_CONFIG_COUNT"].Should().Be("3");
        Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT").Should().BeNull();
    }

    [Fact]
    public void ResolveEnvVars_SessionSelection_ResolvesToProfileEnv()
    {
        var config = Config(new GitProfile { Type = "https", Token = "ghp_secret" },
            name: "work", session: "work");
        GitAuthApplier.ResolveEnvVars(config)["GIT_CONFIG_COUNT"].Should().Be("3");
    }
}
