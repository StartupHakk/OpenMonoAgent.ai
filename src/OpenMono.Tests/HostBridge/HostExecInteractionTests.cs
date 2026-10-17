using FluentAssertions;
using OpenMono.HostBridge;

namespace OpenMono.Tests.HostBridge;

public class HostExecInteractionTests
{
    [Theory]
    [InlineData("ssh user@host", InteractiveHint.TtySecret)]
    [InlineData("scp a b", InteractiveHint.TtySecret)]
    [InlineData("sudo -S systemctl restart api", InteractiveHint.StdinSecret)]
    [InlineData("mysql -p -e 'show tables'", InteractiveHint.StdinSecret)]
    [InlineData("read -p 'Name: ' v", InteractiveHint.StdinSecret)]
    [InlineData("rm -i file", InteractiveHint.StdinConfirm)]
    [InlineData("cat /dev/tty", InteractiveHint.TtyRisk)]
    [InlineData("git pull --ff-only", InteractiveHint.None)]
    [InlineData("ls -la", InteractiveHint.None)]
    public void Classify_MatchesExpectedHint(string command, InteractiveHint expected)
    {
        HostExecInteraction.Classify(command).Should().Be(expected);
    }

    [Theory]
    [InlineData("Password:", PromptKind.Secret)]
    [InlineData("[sudo] password for bob:", PromptKind.Secret)]
    [InlineData("Enter passphrase:", PromptKind.Secret)]
    [InlineData("Are you sure? [y/N]", PromptKind.Confirm)]
    [InlineData("Proceed? (yes/no)", PromptKind.Confirm)]
    [InlineData("Username:", PromptKind.Text)]
    [InlineData("building...\nstill building...", PromptKind.None)]
    [InlineData("Downloaded 42 packages", PromptKind.None)]
    public void ClassifyPrompt_MatchesExpectedKind(string tail, PromptKind expected)
    {
        HostExecInteraction.ClassifyPrompt(tail).Should().Be(expected);
    }

    private sealed class ScriptedPrompter : IHostExecPrompter
    {
        public bool CanPrompt { get; set; } = true;
        public List<string> Seen { get; } = [];
        public string TextAnswer { get; set; } = "bob";
        public bool ConfirmAnswer { get; set; } = true;

        public Task<char[]?> AskSecretAsync(string promptExcerpt, CancellationToken ct)
        {
            Seen.Add("secret:" + promptExcerpt);
            return Task.FromResult<char[]?>("s3cr3t".ToCharArray());
        }

        public Task<string?> AskTextAsync(string promptExcerpt, CancellationToken ct)
        {
            Seen.Add("text:" + promptExcerpt);
            return Task.FromResult<string?>(TextAnswer);
        }

        public Task<bool?> AskConfirmAsync(string promptExcerpt, CancellationToken ct)
        {
            Seen.Add("confirm:" + promptExcerpt);
            return Task.FromResult<bool?>(ConfirmAnswer);
        }
    }

    private static HostExecutor Executor() => new(new HostExecPolicy
    {
        Allow = ["*"],
        Deny = [],
        Default = "allow",
    }, Path.GetTempPath(), Path.GetTempPath(),
    new HostIdentity("", SudoPolicy.Deny, password: null));

    [Fact]
    public async Task TextPrompt_IsForwardedToStdin()
    {
        // NB: bash `read -p` gates its prompt on a TTY, so tests emulate real
        // prompters (mysql -p, input(), sudo -S) with printf + a blocking read.
        var prompter = new ScriptedPrompter { TextAnswer = "bob" };
        var output = await Executor().ExecuteAsync(
            new HostExecRequest("printf 'Enter name: '; read name; echo \"hello $name\"", null, false, false),
            30_000, CancellationToken.None, prompter);

        output.Should().Contain("hello bob");
        prompter.Seen.Should().ContainSingle(s => s.StartsWith("text:") && s.Contains("Enter name"));
    }

    [Fact]
    public async Task ConfirmPrompt_AnswersYes()
    {
        var prompter = new ScriptedPrompter { ConfirmAnswer = true };
        var output = await Executor().ExecuteAsync(
            new HostExecRequest("printf 'Proceed? (yes/no) '; read yn; echo \"said $yn\"", null, false, false),
            30_000, CancellationToken.None, prompter);

        output.Should().Contain("said y");
    }

    [Fact]
    public async Task SecretPrompt_UsesMaskedChannel()
    {
        var prompter = new ScriptedPrompter();
        var output = await Executor().ExecuteAsync(
            new HostExecRequest("printf 'Password: '; read -s pw; echo \"got ${#pw} chars\"", null, false, false),
            30_000, CancellationToken.None, prompter);

        output.Should().Contain("got 6 chars");
        prompter.Seen.Should().ContainSingle(s => s.StartsWith("secret:"));
    }

    [Fact]
    public async Task PromptWithoutPrompter_FailsFastWithGuidance()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var output = await Executor().ExecuteAsync(
            new HostExecRequest("printf 'Enter name: '; read name; echo hi", null, false, false),
            30_000, CancellationToken.None, prompter: null);
        sw.Stop();

        output.Should().StartWith("ERROR:").And.Contain("interactive input").And.Contain("non-interactive");
        // Fail-fast, not a 30s hang (settle 400ms + margin).
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task NonInteractiveCommand_UnchangedWithoutPrompter()
    {
        var output = await Executor().ExecuteAsync(
            new HostExecRequest("echo hello", null, false, false),
            30_000, CancellationToken.None, prompter: null);

        output.Should().Contain("hello");
        output.Should().NotStartWith("ERROR:");
    }
}
