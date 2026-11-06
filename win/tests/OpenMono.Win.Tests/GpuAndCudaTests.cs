using FluentAssertions;
using OpenMono.Win.Foundry;

namespace OpenMono.Win.Tests;

public sealed class GpuCatalogTests
{
    [Fact]
    public void ParsesNvidiaSmiRows()
    {
        const string output = "NVIDIA GeForce RTX 4090, 24564\nNVIDIA GeForce RTX 5090, 32607\n";

        var gpus = GpuCatalog.ParseNvidiaSmi(output);

        gpus.Should().HaveCount(2);
        gpus[0].Name.Should().Be("NVIDIA GeForce RTX 4090");
        gpus[0].DedicatedVramBytes.Should().Be(24564L * 1024 * 1024);
        gpus.All(g => g.IsNvidia).Should().BeTrue();
    }

    [Fact]
    public void SkipsMalformedSmiRows()
    {
        const string output = "garbage line\nNVIDIA RTX 6000, notanumber\nNVIDIA H100, 81559\n";

        var gpus = GpuCatalog.ParseNvidiaSmi(output);

        gpus.Should().ContainSingle().Which.Name.Should().Be("NVIDIA H100");
    }

    [Fact]
    public void ParsesWmicCsv()
    {
        const string output = "Node,AdapterRAM,Name\nPC,4294967296,NVIDIA GeForce RTX 4090\n";

        var gpus = GpuCatalog.ParseWmicCsv(output);

        gpus.Should().ContainSingle().Which.Name.Should().Be("NVIDIA GeForce RTX 4090");
    }

    [Fact]
    public void SystemProberPrefersNvidiaSmi()
    {
        var runner = new FakeCliRunner();
        runner.Respond(
            "nvidia-smi",
            "--query-gpu=name,memory.total --format=csv,noheader,nounits",
            0,
            "NVIDIA GeForce RTX 4090, 24564\n");

        var gpus = new SystemGpuProber(runner).ProbeAsync().GetAwaiter().GetResult();

        gpus.Should().ContainSingle();
        runner.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task SystemProberReturnsEmptyWithoutDriver()
    {
        var runner = new FakeCliRunner();
        // nvidia-smi missing: no queued response, and this Linux CI box is not Windows.

        var gpus = await new SystemGpuProber(runner).ProbeAsync();

        gpus.Should().BeEmpty();
    }
}

public sealed class CudaCheckerTests
{
    private static CudaCheckInput FakeTree(bool cuda, bool cudnn) => new(
        ["/cuda/bin", "/cudnn/bin"],
        dir => dir switch
        {
            "/cuda/bin" when cuda => ["cublas64_12.dll", "cudart64_12.dll"],
            "/cudnn/bin" when cudnn => ["cudnn64_9.dll"],
            _ => [],
        });

    [Fact]
    public void AllPresentIsOk()
    {
        var result = CudaPrerequisiteChecker.Check(FakeTree(true, true), smiFound: true);

        result.Ok.Should().BeTrue();
        result.Issues.Should().BeEmpty();
    }

    [Fact]
    public void MissingCudnnExplainsRemediation()
    {
        var result = CudaPrerequisiteChecker.Check(FakeTree(true, false), smiFound: true);

        result.Ok.Should().BeFalse();
        result.CudaDllFound.Should().BeTrue();
        result.CudnnDllFound.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Contains("cudnn64"));
        result.Remediation.Should().Contain("cuDNN").And.Contain("PATH");
    }

    [Fact]
    public void MissingDriverIsReported()
    {
        var result = CudaPrerequisiteChecker.Check(FakeTree(true, true), smiFound: false);

        result.Ok.Should().BeFalse();
        result.Issues.Should().Contain(i => i.Contains("nvidia-smi"));
    }
}
