using FluentAssertions;
using OpenMono.Llm;

namespace OpenMono.Tests.Llm;

public class ProviderRegistryTests
{
    [Fact]
    public void Lists_MosaicMimo_AndKeepsStrataQwen()
    {
        var reg = new ProviderRegistry();
        var models = reg.ListModels();
        models.Should().Contain("mosaic/MiMo-V2.6-Flash");
        models.Should().Contain("strata/Qwen3.8-Flash-Next");
        reg.Resolve("mosaic").Should().NotBeNull();
        reg.Resolve("strata").Should().NotBeNull();
    }
}
