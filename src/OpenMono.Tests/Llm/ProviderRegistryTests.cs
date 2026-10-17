using FluentAssertions;
using OpenMono.Llm;

namespace OpenMono.Tests.Llm;

public class ProviderRegistryTests
{
    [Fact]
    public void ListsStrataAndMosaicFamilies()
    {
        var registry = new ProviderRegistry();

        registry.Resolve("strata").Should().NotBeNull();
        registry.Resolve("mosaic").Should().NotBeNull();
        registry.Resolve("mosaic")!.SupportedModels.Should().Contain("GLM-4.5-Air");
        registry.ListModels().Should().Contain("mosaic/GLM-4.5-Air");
        registry.ListModels().Should().Contain("strata/Qwen3.8-Flash-Next");
    }

    [Fact]
    public void MosaicCreateClient_UsesPort8090ByDefault()
    {
        MosaicProvider.DefaultEndpoint.Should().Be("http://localhost:8090");
        var registry = new ProviderRegistry();
        var client = registry.Resolve("mosaic")!.CreateClient(new ProviderConfig { Name = "mosaic" });
        client.Should().BeOfType<OpenAiCompatClient>();
        client.Dispose();
    }
}
