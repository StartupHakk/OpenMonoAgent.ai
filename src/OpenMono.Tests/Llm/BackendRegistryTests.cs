using FluentAssertions;
using OpenMono.Llm;

namespace OpenMono.Tests.Llm;

public class BackendRegistryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"openmono-bkreg-{Guid.NewGuid():N}");

    public BackendRegistryTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private BackendDefinition Def(string name, string endpoint = "http://localhost:7474", string? model = null, bool local = true) =>
        new() { Name = name, Endpoint = endpoint, DefaultModel = model, IsLocal = local };

    [Fact]
    public void Add_Get_Has_AreCaseInsensitive()
    {
        var r = new BackendRegistry();
        r.Add(Def("default"));
        r.Has("DEFAULT").Should().BeTrue();
        r.Get("Default")?.Endpoint.Should().Be("http://localhost:7474");
        r.All.Should().HaveCount(1);
    }

    [Fact]
    public void Add_RequiresNameAndEndpoint()
    {
        var r = new BackendRegistry();
        var bad = new BackendDefinition { Name = "", Endpoint = "http://x" };
        var act = () => r.Add(bad);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Remove_AlsoDropsKey()
    {
        var r = new BackendRegistry();
        r.Add(Def("relay", local: false));
        r.SetKey("relay", "sk-1");
        r.HasKey("relay").Should().BeTrue();
        r.Remove("relay");
        r.Has("relay").Should().BeFalse();
        r.HasKey("relay").Should().BeFalse();
    }

    [Fact]
    public void Keys_ArePersisted_AndRestoredFromDisk()
    {
        var r = new BackendRegistry();
        r.Add(Def("relay", "https://api.example", model: "gpt-4o", local: false));
        r.SetKey("relay", "sk-secret");

        var path = Path.Combine(_tempDir, "backends.json");
        r.SaveToFile(path);

        var onDisk = File.ReadAllText(path);
        onDisk.Should().Contain("sk-secret", "keys are now persisted alongside backend defs");
        onDisk.Should().Contain("\"default_model\": \"gpt-4o\"");

        var r2 = new BackendRegistry();
        r2.LoadFromFile(path);
        r2.Get("relay")?.DefaultModel.Should().Be("gpt-4o");
        r2.HasKey("relay").Should().BeTrue("keys are restored from disk");
        r2.GetKey("relay").Should().Be("sk-secret");
    }

    [Fact]
    public void LoadFromFile_IgnoresMissingAndMalformedEntries()
    {
        var path = Path.Combine(_tempDir, "backends.json");
        File.WriteAllText(path, """
        {
          "a": { "endpoint": "http://a", "default_model": "m-a" },
          "b": "not-an-object",
          "c": { "no_endpoint": true }
        }
        """);

        var r = new BackendRegistry();
        r.LoadFromFile(path);

        r.Has("a").Should().BeTrue();
        r.Get("a")!.DefaultModel.Should().Be("m-a");
        r.Has("b").Should().BeFalse();
        r.Has("c").Should().BeFalse();
    }

    [Fact]
    public void RemapLoopbackEndpoints_RewritesLocalLoopbackOnly()
    {
        var r = new BackendRegistry();
        r.Add(Def("local", "http://localhost:7474"));
        r.Add(Def("127", "http://127.0.0.1:11434"));
        r.Add(Def("named", "http://llama-server:7474"));
        r.Add(Def("remote", "http://localhost:10422", local: false));
        r.Add(Def("remote2", "https://api.example/v1", local: false));

        r.RemapLoopbackEndpoints("host.docker.internal");

        r.Get("local")!.Endpoint.Should().Be("http://host.docker.internal:7474");
        r.Get("127")!.Endpoint.Should().Be("http://host.docker.internal:11434");
        r.Get("named")!.Endpoint.Should().Be("http://llama-server:7474", "non-loopback hosts stay put");
        r.Get("remote")!.Endpoint.Should().Be("http://localhost:10422", "non-local backends stay put");
        r.Get("remote2")!.Endpoint.Should().Be("https://api.example/v1");
    }

    [Fact]
    public void LoadFromFile_MissingFile_IsNoOp()
    {
        var r = new BackendRegistry();
        r.LoadFromFile(Path.Combine(_tempDir, "does-not-exist.json"));
        r.All.Should().BeEmpty();
    }

    [Fact]
    public void SaveToFile_WritesReadableDefinitionsOnly()
    {
        var r = new BackendRegistry();
        r.Add(Def("default", model: "qwen"));
        var path = Path.Combine(_tempDir, "backends.json");
        r.SaveToFile(path);
        File.Exists(path).Should().BeTrue();
    }
}
