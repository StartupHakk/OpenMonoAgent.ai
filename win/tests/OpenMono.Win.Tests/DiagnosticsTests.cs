using System.IO.Compression;
using FluentAssertions;
using OpenMono.Win.Launcher;
using Xunit;

namespace OpenMono.Win.Tests;

public sealed class DiagnosticsRedactorTests
{
    private const string Home = "C:\\Users\\spencer";

    [Fact]
    public void HomePathBecomesTilde()
    {
        DiagnosticsRedactor.RedactText("log at C:\\Users\\spencer\\.foundry\\logs and C:/Users/spencer/x", Home)
            .Should().Be("log at ~\\.foundry\\logs and ~/x");
    }

    [Theory]
    [InlineData("OPENMONO_API_KEY=abc123", "OPENMONO_API_KEY=[redacted]")]
    [InlineData("\"api_key\": \"abc123\"", "\"api_key\": \"[redacted]\"")]
    [InlineData("pair_secret: s3cr3t", "pair_secret: [redacted]")]
    [InlineData("Authorization: Bearer eyJhbGciOi.payload.sig", "Authorization: Bearer [redacted]")]
    [InlineData("key sk-abcdefghijklmnopqrstuvwx used", "key [redacted] used")]
    public void SecretsAreRemoved(string input, string expected)
    {
        DiagnosticsRedactor.RedactText(input, Home).Should().Be(expected);
    }

    [Fact]
    public void OrdinaryTextIsKept()
    {
        const string line = "Server healthy on http://127.0.0.1:7474. Serving qwen3.8-27b-cuda-gpu:1.";

        DiagnosticsRedactor.RedactText(line, Home).Should().Be(line);
    }

    [Fact]
    public void SettingsSecretsAreScrubbedStructurally()
    {
        var redacted = DiagnosticsRedactor.RedactSettingsJson(
            """{"llm":{"endpoint":"http://127.0.0.1:7474","api_key":"abc","model":"m"},"web":{"gateway":"g"},"host":{"pair_secret":"p","invite_token":"i"}}""",
            Home);

        redacted.Should().NotContain("\"abc\"").And.NotContain("\"p\"").And.NotContain("\"i\"");
        redacted.Should().Contain("http://127.0.0.1:7474").And.Contain("\"model\": \"m\"");
    }

    [Fact]
    public void InvalidSettingsAreOmitted()
    {
        DiagnosticsRedactor.RedactSettingsJson("{ nope", Home).Should().Contain("omitted");
    }
}

public sealed class DiagnosticsBundleTests
{
    [Fact]
    public async Task BundleHasEveryPieceAndNoSecrets()
    {
        using var rig = new FoundryRig();
        rig.WriteSettings("""{"llm":{"endpoint":"http://127.0.0.1:7474","api_key":"supersecretvalue"}}""");
        rig.Runner.Respond(FoundryRig.Exe, "server logs --lines 200", 0,
            $"loaded model from {rig.Home}/.foundry/cache with token=abc123\n");
        var doctor = new[] { new DoctorCheck("docker", CheckLevel.Ok, "Docker engine 28.1.1.") };

        var files = await new DiagnosticsBundle(rig.Deps()).CollectAsync(
            doctor, ["C:\\Program Files\\NVIDIA GPU Computing Toolkit\\CUDA\\v12.8\\bin", "C:\\Windows"]);

        files.Keys.Should().BeEquivalentTo(
            "environment.txt", "cuda-path.txt", "foundry-status.txt", "foundry-server-log.txt",
            "doctor.txt", "inference.json", "settings.redacted.json");
        files["environment.txt"].Should().Contain("RTX 4090").And.Contain("foundry 0.13.0");
        files["cuda-path.txt"].Should().Contain("CUDA\\v12.8\\bin").And.NotContain("C:\\Windows");
        files["foundry-server-log.txt"].Should().Contain("~/.foundry/cache").And.NotContain("abc123");
        files.Values.Should().NotContain(v => v.Contains("supersecretvalue") || v.Contains(rig.Home));

        var zip = Path.Combine(rig.Home, "out", "diag.zip");
        DiagnosticsBundle.WriteZip(zip, files);
        using var archive = ZipFile.OpenRead(zip);
        archive.Entries.Select(e => e.FullName).Should().HaveCount(7);
    }
}
