using System.Text.Json;
using FluentAssertions;
using OpenMono.Commands;
using OpenMono.Config;
using OpenMono.Llm;
using OpenMono.Rendering;
using OpenMono.Session;
using OpenMono.Tools;
using Xunit;

using OpenMono.Tests.Rendering;

namespace OpenMono.Tests.Commands;

public class GitCommandTests
{
    private static CommandContext Context(out SessionState session, AppConfig cfg, IRenderer? renderer = null)
    {
        session = new SessionState { Id = "sess_test" };
        return new CommandContext
        {
            Session = session,
            ToolRegistry = new ToolRegistry(),
            CommandRegistry = new CommandRegistry(),
            Config = cfg,
            Renderer = renderer ?? new TerminalRenderer(),
            WorkingDirectory = "/workspace",
            Llm = new NoopLlm(),
        };
    }

    private static AppConfig GitConfig(params (string Name, string Type, string? Extra)[] profiles)
    {
        var git = new GitConfig();
        foreach (var (name, type, _) in profiles)
            git.Profiles[name] = new GitProfile { Type = type };
        return new AppConfig { Git = git };
    }

    [Fact]
    public async Task Off_ClearsProfile()
    {
        var cfg = GitConfig(("work", "ssh", null), ("personal", "https", null));
        var ctx = Context(out var session, cfg);
        session.Meta.GitProfileName = "work";

        await new GitCommand().ExecuteAsync(["off"], ctx, CancellationToken.None);

        session.Meta.GitProfileName.Should().BeNull();
    }

    [Fact]
    public async Task SwitchToKnownProfile_SetsProfile()
    {
        var cfg = GitConfig(("work", "ssh", null), ("personal", "https", null));
        var ctx = Context(out var session, cfg);

        await new GitCommand().ExecuteAsync(["personal"], ctx, CancellationToken.None);

        session.Meta.GitProfileName.Should().Be("personal");
    }

    [Fact]
    public async Task SwitchToUnknownProfile_LeavesProfileUnchanged()
    {
        var cfg = GitConfig(("work", "ssh", null));
        var ctx = Context(out var session, cfg);
        session.Meta.GitProfileName = "work";

        await new GitCommand().ExecuteAsync(["nope"], ctx, CancellationToken.None);

        session.Meta.GitProfileName.Should().Be("work");
    }

    [Fact]
    public async Task SwitchToInvalidType_LeavesProfileUnchanged()
    {
        var cfg = GitConfig(("work", "ssh", null), ("bad", "carrier-pigeon", null));
        var ctx = Context(out var session, cfg);
        session.Meta.GitProfileName = "work";

        await new GitCommand().ExecuteAsync(["bad"], ctx, CancellationToken.None);

        session.Meta.GitProfileName.Should().Be("work");
    }

    private sealed class NoopLlm : ILlmClient
    {
        public IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages, JsonElement? tools, LlmOptions options,
            CancellationToken ct)
            => throw new NotImplementedException();
        public void Dispose() { }
    }
}

/// <summary>
/// The /git add wizard: an interactive walk-through that persists a new profile
/// to the user-level settings.json. Each test relocates HOME to a temp dir so
/// the SSH-key listing/validation and settings path are deterministic.
/// </summary>
public sealed class GitAddWizardTests : IDisposable
{
    private readonly string _tempHome;
    private readonly string _oldHome;
    private static readonly string Workdir =
        Path.Combine(Path.GetTempPath(), $"openmono-git-wiz-wd-{Guid.NewGuid():N}");

    public GitAddWizardTests()
    {
        _oldHome = Environment.GetEnvironmentVariable("HOME") ?? "";
        _tempHome = Path.Combine(Path.GetTempPath(), $"openmono-git-wizard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempHome, ".ssh"));
        Environment.SetEnvironmentVariable("HOME", _tempHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("HOME", _oldHome);
        try { Directory.Delete(_tempHome, recursive: true); } catch { }
    }

    private static CommandContext Wctx(AppConfig cfg, FakeScriptedRenderer renderer, out SessionState session)
    {
        session = new SessionState { Id = "sess_wiz" };
        return new CommandContext
        {
            Session = session,
            ToolRegistry = new ToolRegistry(),
            CommandRegistry = new CommandRegistry(),
            Config = cfg,
            Renderer = renderer,
            WorkingDirectory = Workdir,
            Llm = new NoopLlm2(),
        };
    }

    private static AppConfig WizardConfig(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        return new AppConfig { DataDirectory = dataDir, Git = new GitConfig() };
    }

    private static void SeedSshKey(string name)
        => File.WriteAllText(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", name),
            "KEYDATA");

    [Fact]
    public async Task SshWizard_PersistsProfileAndActivatesIt()
    {
        var dataDir = Path.Combine(_tempHome, "data");
        var cfg = WizardConfig(dataDir);
        SeedSshKey("id_ed25519");

        var renderer = new FakeScriptedRenderer();
        renderer.Enqueue(
            "work",               // name
            "1",                  // type ssh
            "id_ed25519",         // key (bare name → normalized to ~/.ssh/id_ed25519)
            "Jane",               // user.name
            "jane@example.com",   // email
            "y",                  // confirm
            "y");                 // activate
        var ctx = Wctx(cfg, renderer, out var session);

        await new GitCommand().ExecuteAsync(["add"], ctx, CancellationToken.None);

        cfg.Git.Profiles.Should().ContainKey("work");
        cfg.Git.Profiles["work"].Type.Should().Be("ssh");
        cfg.Git.Profiles["work"].SshKey.Should().Be("~/.ssh/id_ed25519");
        cfg.Git.Profiles["work"].User.Should().Be("Jane");
        cfg.Git.Profiles["work"].Email.Should().Be("jane@example.com");

        session.Meta.GitProfileName.Should().Be("work");
        session.Meta.GitProfileTouched.Should().BeTrue();

        var settings = Path.Combine(dataDir, "settings.json");
        File.Exists(settings).Should().BeTrue();
        using var doc = JsonDocument.Parse(File.ReadAllText(settings));
        doc.RootElement.GetProperty("git").GetProperty("profiles")
            .GetProperty("work").GetProperty("ssh_key").GetString().Should().Be("~/.ssh/id_ed25519");
        renderer.Wrote("Profile 'work' saved and active").Should().BeTrue();
    }

    [Fact]
    public async Task HttpsWizard_TokenEnv_PersistsEnvRefNotSecret()
    {
        var dataDir = Path.Combine(_tempHome, "data2");
        var cfg = WizardConfig(dataDir);

        var renderer = new FakeScriptedRenderer();
        renderer.Enqueue(
            "work",               // name
            "2",                  // type https
            "gitlab.example.com", // host
            "1",                  // token source: env var
            "GITLAB_PAT",         // env var name
            "Jane",               // user.name
            "jane@example.com",   // email
            "y",                  // confirm
            "y");                 // activate
        var ctx = Wctx(cfg, renderer, out _);

        await new GitCommand().ExecuteAsync(["add"], ctx, CancellationToken.None);

        cfg.Git.Profiles["work"].Type.Should().Be("https");
        cfg.Git.Profiles["work"].Host.Should().Be("gitlab.example.com");
        cfg.Git.Profiles["work"].TokenEnv.Should().Be("GITLAB_PAT");
        cfg.Git.Profiles["work"].Token.Should().BeNull();

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDir, "settings.json")));
        var profile = doc.RootElement.GetProperty("git").GetProperty("profiles").GetProperty("work");
        profile.GetProperty("token_env").GetString().Should().Be("GITLAB_PAT");
        profile.TryGetProperty("token", out _).Should().BeFalse();
    }

    [Fact]
    public async Task HttpsWizard_PastedToken_IsMaskedPromptAndStored()
    {
        var dataDir = Path.Combine(_tempHome, "data3");
        var cfg = WizardConfig(dataDir);

        var renderer = new FakeScriptedRenderer();
        renderer.Enqueue(
            "work",                     // name
            "2",                        // type https
            "",                         // host → github.com
            "2",                        // token source: paste
            "ghp_supersecretpat123",    // the (masked) token
            "Jane",                     // user.name
            "jane@example.com",         // email
            "y",                        // confirm
            "n");                       // do not activate
        var ctx = Wctx(cfg, renderer, out var session);

        await new GitCommand().ExecuteAsync(["add", "work"], ctx, CancellationToken.None);

        cfg.Git.Profiles["work"].Token.Should().Be("ghp_supersecretpat123");
        cfg.Git.Profiles["work"].Host.Should().Be("github.com");
        session.Meta.GitProfileTouched.Should().BeFalse();

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataDir, "settings.json")));
        doc.RootElement.GetProperty("git").GetProperty("profiles")
            .GetProperty("work").GetProperty("token").GetString().Should().Be("ghp_supersecretpat123");
    }

    [Fact]
    public async Task Wizard_SshKeyOutsideDotSsh_IsRejected()
    {
        var dataDir = Path.Combine(_tempHome, "data4");
        var cfg = WizardConfig(dataDir);
        Directory.CreateDirectory(Path.Combine(_tempHome, "elsewhere"));
        File.WriteAllText(Path.Combine(_tempHome, "elsewhere", "k"), "KEY");

        var renderer = new FakeScriptedRenderer();
        renderer.Enqueue(
            "work",                                  // name
            "1",                                     // type ssh
            Path.Combine(_tempHome, "elsewhere", "k"), // rejected
            "n");                                    // give up → cancelled
        var ctx = Wctx(cfg, renderer, out _);

        await new GitCommand().ExecuteAsync(["add"], ctx, CancellationToken.None);

        renderer.Wrote("must live under ~/.ssh").Should().BeTrue();
        cfg.Git.Profiles.Should().BeEmpty();
        File.Exists(Path.Combine(dataDir, "settings.json")).Should().BeFalse();
    }

    [Fact]
    public async Task Wizard_NonInteractive_WarnsAndWritesNothing()
    {
        var dataDir = Path.Combine(_tempHome, "data5");
        var cfg = WizardConfig(dataDir);

        var renderer = new FakeScriptedRenderer { Interactive = false };
        var ctx = Wctx(cfg, renderer, out _);

        await new GitCommand().ExecuteAsync(["add"], ctx, CancellationToken.None);

        renderer.Wrote("needs an interactive terminal").Should().BeTrue();
        cfg.Git.Profiles.Should().BeEmpty();
        File.Exists(Path.Combine(dataDir, "settings.json")).Should().BeFalse();
    }

    [Fact]
    public async Task Wizard_OverwritePrompt_CancellingKeepsOriginal()
    {
        var dataDir = Path.Combine(_tempHome, "data6");
        var cfg = WizardConfig(dataDir);
        cfg.Git.Profiles["work"] = new GitProfile { Type = "ssh", SshKey = "~/.ssh/orig" };

        var renderer = new FakeScriptedRenderer();
        renderer.Enqueue(
            "work", // same name → overwrite prompt
            "n");   // do not overwrite → cancelled
        var ctx = Wctx(cfg, renderer, out _);

        await new GitCommand().ExecuteAsync(["add"], ctx, CancellationToken.None);

        renderer.Wrote("already exists").Should().BeTrue();
        cfg.Git.Profiles["work"].SshKey.Should().Be("~/.ssh/orig");
        renderer.Wrote("Cancelled").Should().BeTrue();
    }

    [Fact]
    public async Task PlainGitList_SuggestsWizard()
    {
        var cfg = WizardConfig(Path.Combine(_tempHome, "data7"));
        var renderer = new FakeScriptedRenderer();
        var ctx = Wctx(cfg, renderer, out _);

        await new GitCommand().ExecuteAsync([], ctx, CancellationToken.None);

        renderer.Wrote("Create one with: /git add").Should().BeTrue();
    }

    private sealed class NoopLlm2 : ILlmClient
    {
        public IAsyncEnumerable<StreamChunk> StreamChatAsync(
            IReadOnlyList<Message> messages, JsonElement? tools, LlmOptions options,
            CancellationToken ct)
        => throw new NotImplementedException();
        public void Dispose() { }
    }
}
