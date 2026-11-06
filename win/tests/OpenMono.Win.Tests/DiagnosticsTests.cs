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
        DiagnosticsRedactor.RedactText("log at C:\\Users\\spencer\\.openmono\\win\\llama-server.log and C:/Users/spencer/x", Home)
            .Should().Be("log at ~\\.openmono\\win\\llama-server.log and ~/x");
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
        const string line = "Server healthy on http://127.0.0.1:7474. Serving Qwen3.8-27B-UD-Q4_K_M.";

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
        using var rig = new LlamaRig();
        rig.Env["LLAMA_API_KEY"] = "llamakeyvalue";
        await rig.Orchestrator().UpAsync(new UpOptions());
        rig.WriteSettings("""{"llm":{"endpoint":"http://127.0.0.1:7474","api_key":"supersecretvalue"}}""");
        File.WriteAllText(Path.Combine(rig.Home, ".openmono", "win", "llama-server.log"),
            $"main: loading model {rig.Home}/repo/models/x.gguf\nargs: --api-key llamakeyvalue token=abc123\n");
        var doctor = new[] { new DoctorCheck("docker", CheckLevel.Ok, "Docker engine 28.1.1.") };

        var files = await new DiagnosticsBundle(rig.Deps()).CollectAsync(doctor);

        files.Keys.Should().BeEquivalentTo(
            "environment.txt", "nvidia-smi.txt", "llama-cpp.txt", "llama-server-log.txt",
            "doctor.txt", "inference.json", "settings.redacted.json");
        files["environment.txt"].Should().Contain("RTX 4090");
        files["nvidia-smi.txt"].Should().Contain("CUDA Version: 12.8");
        files["llama-cpp.txt"].Should().Contain("b9000-cuda-12.4");
        files["llama-server-log.txt"].Should().Contain("~/repo/models/x.gguf").And.NotContain("abc123");
        files["inference.json"].Should().Contain("Qwen3.8-27B-UD-Q4_K_M");
        files.Values.Should().NotContain(v => v.Contains("supersecretvalue") || v.Contains("llamakeyvalue") || v.Contains(rig.Home));

        var zip = Path.Combine(rig.Home, "out", "diag.zip");
        DiagnosticsBundle.WriteZip(zip, files);
        using var archive = ZipFile.OpenRead(zip);
        archive.Entries.Select(e => e.FullName).Should().HaveCount(7);
    }
}
