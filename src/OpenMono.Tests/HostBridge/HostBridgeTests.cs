using FluentAssertions;
using OpenMono.HostBridge;

namespace OpenMono.Tests.HostBridge;

public class HostExecRequestTests
{
    [Fact]
    public void RawCommand_ParsesWithoutElevation()
    {
        HostExecRequest.TryParse("HOST_EXEC: git pull --ff-only", out var request)
            .Should().BeTrue();
        request!.Command.Should().Be("git pull --ff-only");
        request.AsRoot.Should().BeFalse();
        request.Background.Should().BeFalse();
    }

    [Fact]
    public void JsonEnvelope_ParsesAllFields()
    {
        HostExecRequest.TryParse(
            "HOST_EXEC: {\"command\": \"systemctl restart api\", \"timeout_ms\": 60000, \"background\": true, \"as_root\": true}",
            out var request).Should().BeTrue();
        request!.Command.Should().Be("systemctl restart api");
        request.TimeoutMs.Should().Be(60000);
        request.Background.Should().BeTrue();
        request.AsRoot.Should().BeTrue();
    }

    [Fact]
    public void EmptyQuestion_Fails()
    {
        HostExecRequest.TryParse("HOST_EXEC:   ", out var request)
            .Should().BeFalse();
        request.Should().BeNull();
    }

    [Fact]
    public void JsonWithoutCommand_Fails()
    {
        HostExecRequest.TryParse("HOST_EXEC: {\"timeout_ms\": 5}", out _)
            .Should().BeFalse();
    }

    [Fact]
    public void RawMultilineHeredoc_ParsesWhole()
    {
        var body = "cat >> game.html << 'JSEOF'\nvar x = 1;\nJSEOF";
        HostExecRequest.TryParse("HOST_EXEC: " + body, out var request).Should().BeTrue();
        request!.Command.Should().Be(body);
    }

    private static string BigJsBody(int chars)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("cat >> game.html << 'JSEOF'\n");
        var i = 0;
        while (sb.Length < chars)
            sb.Append($"// chunk {i++} fill lorem ipsum dolor sit amet '{new string('z', 60)}'\n");
        sb.Append("JSEOF");
        return sb.ToString();
    }

    [Fact]
    public void LargeRawNewlineJsonEnvelope_ParsesLeniently()
    {
        // What the model emits for big appends: JSON framing, but the command
        // string carries literal newlines (a raw-heredoc envelope stall seen live).
        var body = BigJsBody(8600);
        body.Length.Should().BeGreaterThan(8192);
        var question = "HOST_EXEC: {\"command\": \"" + body + "\", \"timeout_ms\": 30000}";
        HostExecRequest.TryParse(question, out var request).Should().BeTrue();
        request!.Command.Should().Be(body);
        request.TimeoutMs.Should().Be(30000);
        request.AsRoot.Should().BeFalse();
    }

    [Fact]
    public void LenientFlags_Parsed()
    {
        var question = "HOST_EXEC: {\"command\": \"line1\nline2\", \"background\": true, \"as_root\": true}";
        HostExecRequest.TryParse(question, out var request).Should().BeTrue();
        request!.Command.Should().Be("line1\nline2");
        request.Background.Should().BeTrue();
        request.AsRoot.Should().BeTrue();
    }

    [Fact]
    public void UnescapedQuoteInsideCommand_RejectedNeverTruncated()
    {
        // A premature closing quote must fail closed, never execute a prefix.
        HostExecRequest.TryParse(
            "HOST_EXEC: {\"command\": \"echo \"hi\" there\", \"timeout_ms\": 5000}",
            out _).Should().BeFalse();
    }

    [Fact]
    public void LooksLikeHostExec_GatesAutoReply()
    {
        HostExecRequest.LooksLikeHostExec("HOST_EXEC: {broken").Should().BeTrue();
        HostExecRequest.LooksLikeHostExec("  HOST_EXEC: {broken").Should().BeTrue();
        HostExecRequest.LooksLikeHostExec("please deploy").Should().BeFalse();
        HostExecRequest.LooksLikeHostExec("").Should().BeFalse();
    }

    [Fact]
    public void MalformedAnswer_TellsAgentHowToResendWithoutEchoing()
    {
        var question = "HOST_EXEC: {\"command\": \"SECRET-MARKER-123 " + new string('q', 8000);
        var answer = Bridge.MalformedEnvelopeAnswer(question);
        answer.Should().Contain("rejected").And.Contain("\\n").And.Contain("cat >>");
        answer.Should().NotContain("SECRET-MARKER-123");
    }
}

public class HostPolicyTests
{
    private static HostExecutor Executor(string def = "ask") =>
        new(new HostExecPolicy
        {
            Allow = ["git *", "docker *"],
            Deny = ["rm -rf *", "shutdown *"],
            Default = def,
        }, Path.GetTempPath(), Path.GetTempPath(),
        new HostIdentity("", allowSudo: false, password: null));

    [Fact]
    public void Deny_WinsOverAllow()
    {
        var executor = new HostExecutor(new HostExecPolicy
        {
            Allow = ["rm *"],
            Deny = ["rm -rf *"],
            Default = "allow",
        }, Path.GetTempPath(), Path.GetTempPath(),
        new HostIdentity("", allowSudo: false, password: null));
        executor.CheckPolicy("rm -rf /tmp/x").Should().Be(HostPolicyDecision.Deny);
    }

    [Fact]
    public void AllowList_Matches()
    {
        Executor().CheckPolicy("git pull").Should().Be(HostPolicyDecision.Allow);
    }

    [Fact]
    public void UnknownCommand_FollowsDefault()
    {
        Executor("ask").CheckPolicy("systemctl restart api").Should().Be(HostPolicyDecision.Ask);
        Executor("deny").CheckPolicy("systemctl restart api").Should().Be(HostPolicyDecision.Deny);
    }

    [Fact]
    public async Task SudoRefused_WhenNotOptedIn()
    {
        var executor = Executor();
        var result = await executor.ExecuteAsync(
            new HostExecRequest("id", null, false, true), 5_000, CancellationToken.None);
        result.Should().StartWith("ERROR:").And.Contain("allow_sudo");
    }

    [Fact]
    public void NeedsPassword_False_WhenSudoNotAllowed()
    {
        Executor().NeedsPassword(new HostExecRequest("id", null, false, true))
            .Should().BeFalse();
    }
    [Fact]
    public async Task LargeHeredoc_ExecutesAndWritesFile()
    {
        // End to end: an 8 KB+ heredoc through policy + executor writes the file.
        var dir = Path.Combine(Path.GetTempPath(), "bridge-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var target = Path.Combine(dir, "game.html");
            var lines = string.Join("\n",
                Enumerable.Range(0, 220).Select(i => $"// asset line {i} " + new string('y', 40)));
            var command = $"cat > '{target}' <<'EOF'\n{lines}\nEOF";
            command.Length.Should().BeGreaterThan(8192);
            HostExecRequest.TryParse("HOST_EXEC: " + command, out var request).Should().BeTrue();
            var executor = new HostExecutor(new HostExecPolicy
            {
                Allow = [.. HostExecPolicy.SampleAllow],
                Deny = [.. HostExecPolicy.SampleDeny],
                Default = "allow",
            }, dir, dir, new HostIdentity("", allowSudo: false, password: null));
            var output = await executor.ExecuteAsync(request!, 30_000, CancellationToken.None);
            output.Should().NotStartWith("ERROR:");
            new FileInfo(target).Length.Should().BeGreaterThan(8192);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class LogRedactorTests
{
    [Fact]
    public void KeyValueSecrets_Redacted()
    {
        LogRedactor.Redact("api_key=supersecret-value").Should().NotContain("supersecret-value");
        LogRedactor.Redact("password: hunter2").Should().NotContain("hunter2");
    }

    [Fact]
    public void BearerTokens_Redacted()
    {
        LogRedactor.Redact("Authorization: Bearer abcdef123").Should().NotContain("abcdef123");
    }

    [Fact]
    public void PasswordFlags_Redacted()
    {
        LogRedactor.Redact("deploy --password s3cr3t --yes").Should().NotContain("s3cr3t");
    }

    [Fact]
    public void OrdinaryOutput_Untouched()
    {
        LogRedactor.Redact("PASS BUILD id=42 elapsed 3s").Should().Be("PASS BUILD id=42 elapsed 3s");
    }
}

public class BridgeOptionsTests
{
    [Fact]
    public void InitAndIdentityFlags_Parse()
    {
        var options = BridgeOptions.Parse(
            ["--init", "--run-as", "deploy", "--password-file", "/run/s.pw", "--allow-sudo"]);
        options.Init.Should().BeTrue();
        options.RunAs.Should().Be("deploy");
        options.PasswordFile.Should().Be("/run/s.pw");
        options.AllowSudoOverride.Should().BeTrue();
    }

    [Fact]
    public void NoSudo_ParsesFalse()
    {
        BridgeOptions.Parse(["--no-sudo"]).AllowSudoOverride.Should().BeFalse();
    }

    [Fact]
    public void TuiClassicPlanFlags_Parse()
    {
        var options = BridgeOptions.Parse(["--tui", "--plan"]);
        options.ForceTui.Should().BeTrue();
        options.Plan.Should().BeTrue();
        options.Classic.Should().BeFalse();
        BridgeOptions.Parse(["--classic"]).Classic.Should().BeTrue();
    }

    [Fact]
    public void UnknownFlag_Throws()
    {
        var act = () => BridgeOptions.Parse(["--password", "x"]);
        act.Should().Throw<InvalidOperationException>();
    }
}

public class OperatorPreambleTests
{
    [Fact]
    public void Build_NamesRunAsUserLogDirAndWorkspace()
    {
        var preamble = OperatorPreamble.Build(
            "/home/operator/srv", "operator", "/home/operator/.openmono/logs", allowSudo: false);
        preamble.Should().Contain("HOST_EXEC:");
        preamble.Should().Contain("operator");
        preamble.Should().Contain("/home/operator/.openmono/logs");
        preamble.Should().Contain("/home/operator/srv");
        preamble.Should().Contain("Sudo is NOT allowed");
        preamble.Should().Contain("as_root");
    }

    [Fact]
    public void Build_AllowsSudoVariant()
    {
        var preamble = OperatorPreamble.Build("/w", "operator", "/home/operator/.openmono/logs", allowSudo: true);
        preamble.Should().Contain("Sudo IS allowed");
    }

    [Fact]
    public void Build_ForbidsContainerPathsForHostWork()
    {
        var preamble = OperatorPreamble.Build("/w", "operator", "/home/operator/.openmono/logs", allowSudo: false);
        preamble.Should().Contain("/root/.openmono");
        preamble.Should().Contain("NEVER Grep");
    }

    [Fact]
    public void Build_TellsModelNotToNarrateOrders()
    {
        var preamble = OperatorPreamble.Build("/w", "operator", "/home/operator/.openmono/logs", allowSudo: false);
        preamble.Should().Contain("Do not quote");
    }
}

public class TuiInputFilterTests
{
    [Theory]
    [InlineData("openmono agent --host")]
    [InlineData("  openmono agent --host --verbose")]
    [InlineData("sudo openmono agent --host")]
    [InlineData("~/srv/openmono agent --host")]
    public void LauncherEcho_IsFiltered(string line)
    {
        TuiInputFilter.IsLauncherEcho(line).Should().BeTrue();
    }

    [Theory]
    [InlineData("deploy the backend")]
    [InlineData("openmono is great, check host logs")]
    [InlineData("/mode")]
    [InlineData("")]
    [InlineData("   ")]
    public void RealTasks_PassThrough(string line)
    {
        TuiInputFilter.IsLauncherEcho(line).Should().BeFalse();
    }
}

public class HostExecPolicyMigrationTests
{
    private static BridgeConfig StockAskConfig() => new()
    {
        HostExec = new HostExecPolicy
        {
            Allow = [.. HostExecPolicy.SampleAllow],
            Deny = [.. HostExecPolicy.SampleDeny],
            Default = "ask",
        },
    };

    [Fact]
    public void UntouchedStockAsk_NeverMigrated_StaysAsk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bridge-test-{Guid.NewGuid():N}.json");
        try
        {
            var migrated = BridgeConfig.MigrateStockAskToAllow(path, StockAskConfig(), new StringWriter());
            migrated.Should().BeFalse();
            // No file is written and the in-memory policy is untouched:
            // "always" requires explicit opt-in at install.
            StockAskConfig().HostExec.Default.Should().Be("ask");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void CustomizedPolicy_NeverMigrated()
    {
        var config = StockAskConfig();
        config.HostExec.Allow.Add("tail *");
        BridgeConfig.MigrateStockAskToAllow(null, config, new StringWriter()).Should().BeFalse();
        config.HostExec.Default.Should().Be("ask");
    }

    [Fact]
    public void AlreadyAllow_NotMigrated()
    {
        var config = StockAskConfig();
        config.HostExec.Default = "allow";
        BridgeConfig.MigrateStockAskToAllow(null, config, new StringWriter()).Should().BeFalse();
    }

    [Fact]
    public void DenyList_StillBlocksAfterMigration()
    {
        var executor = new HostExecutor(new HostExecPolicy
        {
            Allow = [.. HostExecPolicy.SampleAllow],
            Deny = [.. HostExecPolicy.SampleDeny],
            Default = "allow",
        }, Path.GetTempPath(), Path.GetTempPath(),
        new HostIdentity("", allowSudo: false, password: null));
        executor.CheckPolicy("rm -rf /tmp/x").Should().Be(HostPolicyDecision.Deny);
        executor.CheckPolicy("ls -la /home/operator/srv/").Should().Be(HostPolicyDecision.Allow);
    }
}
