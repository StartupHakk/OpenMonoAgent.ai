using OpenMono.Windows.Hardware;

namespace OpenMono.Windows.Tests;

public sealed class TierSelectionTests
{
    private static GpuInfo Nvidia(long gib) =>
        new($"NVIDIA Test {gib}GB", GpuVendor.Nvidia, gib * ModelTierSelector.Gb, 0, "560.0");

    [Fact]
    public void Tier24_On_24Gb_Nvidia_With_Cuda()
    {
        var selection = ModelTierSelector.Select([Nvidia(24)], 64 * ModelTierSelector.Gb, cudaAvailable: true);
        Assert.Equal(InferenceTier.Tier24Gb, selection.Tier);
        Assert.Equal(ModelTierSelector.ServerFlavor.Cuda, selection.Flavor);
    }

    [Fact]
    public void Tier16_On_16Gb_With_Warning()
    {
        var selection = ModelTierSelector.Select([Nvidia(16)], 64 * ModelTierSelector.Gb, cudaAvailable: true);
        Assert.Equal(InferenceTier.Tier16Gb, selection.Tier);
        Assert.NotEmpty(selection.Warnings);
    }

    [Fact]
    public void Tier12_On_12Gb()
    {
        var selection = ModelTierSelector.Select([Nvidia(12)], 64 * ModelTierSelector.Gb, cudaAvailable: false);
        Assert.Equal(InferenceTier.Tier12Gb, selection.Tier);
        Assert.Equal(ModelTierSelector.ServerFlavor.Vulkan, selection.Flavor);
    }

    [Fact]
    public void Cpu_When_Vram_Below_Minimum()
    {
        var selection = ModelTierSelector.Select([Nvidia(8)], 64 * ModelTierSelector.Gb, cudaAvailable: true);
        Assert.Equal(InferenceTier.Cpu, selection.Tier);
        Assert.Equal(ModelTierSelector.ServerFlavor.Cpu, selection.Flavor);
    }

    [Fact]
    public void Cpu_When_No_Gpu_With_Low_Ram_Warning()
    {
        var selection = ModelTierSelector.Select([], 8 * ModelTierSelector.Gb, cudaAvailable: false);
        Assert.Equal(InferenceTier.Cpu, selection.Tier);
        Assert.Contains(selection.Warnings, w => w.Contains("20GB", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(25000, InferenceTier.Tier24Gb)]
    [InlineData(24000, InferenceTier.Tier24Gb)]
    [InlineData(16000, InferenceTier.Tier16Gb)]
    [InlineData(12000, InferenceTier.Tier12Gb)]
    [InlineData(11999, InferenceTier.Cpu)]
    [InlineData(0, InferenceTier.Cpu)]
    public void TierFromVramMegabytes_Matches_InstallSh_Thresholds(long vramMb, InferenceTier expected)
    {
        Assert.Equal(expected, ModelTierSelector.TierFromVramMegabytes(vramMb));
    }

    [Fact]
    public void Parses_NvidiaSmi_Csv()
    {
        var gpus = GpuDetector.ParseNvidiaSmi("NVIDIA GeForce RTX 4090, 24564, 560.94\n");
        var gpu = Assert.Single(gpus);
        Assert.Equal(GpuVendor.Nvidia, gpu.Vendor);
        Assert.True(gpu.DedicatedBytes > 24 * ModelTierSelector.Gb);
        Assert.Equal("560.94", gpu.DriverVersion);
    }

    [Fact]
    public void Classifies_Amd_And_Intel()
    {
        Assert.Equal(GpuVendor.Amd, GpuDetector.ClassifyVendor("AMD Radeon RX 7900 XTX"));
        Assert.Equal(GpuVendor.Intel, GpuDetector.ClassifyVendor("Intel Arc A770"));
        Assert.Equal(GpuVendor.Unknown, GpuDetector.ClassifyVendor("Some Virtual Adapter"));
    }

    [Fact]
    public void Threads_Prefer_Physical_Cores()
    {
        Assert.Equal(8, MemoryDetector.RecommendThreads(8, 16));
        Assert.Equal(16, MemoryDetector.RecommendThreads(0, 16));
    }

    [Fact]
    public void Disk_Requires_10_Percent_Overhead()
    {
        long model = 16L * 1024 * 1024 * 1024;
        long mmproj = 1L * 1024 * 1024 * 1024;
        Assert.True(DiskDetector.HasRoom((long)((model + mmproj) * 1.10), model, mmproj));
        Assert.False(DiskDetector.HasRoom((long)((model + mmproj) * 1.09), model, mmproj));
    }
}
