using FluentAssertions;
using OpenMono.Moe.Family;
using OpenMono.Moe.Quant;
using OpenMono.Moe.Runtime;

namespace OpenMono.Tests.Moe;

public class ExpertCacheTests
{
    [Fact]
    public void Arena_GetAfterPin_DoesNotNeedTheSourceFile()
    {
        var dir = Directory.CreateTempSubdirectory("mosaic-arena");
        try
        {
            var adapter = new MimoV26FlashAdapter();
            var weights = Enumerable.Range(0, 16).Select(i => i * 0.05f).ToArray();
            var blob = ExpertPacker.Pack(adapter, 1, 2, ExpertRole.Gate, weights, 4, 4);
            var path = Path.Combine(dir.FullName, "e.oms1");
            File.WriteAllBytes(path, blob);

            var arena = new RamArena();
            arena.PinFromDisk([path]);
            arena.Seal();
            File.Delete(path);
            Directory.Delete(dir.FullName, recursive: true);

            var key = new ExpertKey(MosaicFamily.MimoV26Flash, 1, 2, ExpertRole.Gate);
            var pinned = arena.Get(key);
            pinned.Length.Should().Be(blob.Length);
            pinned.Span.ToArray().Should().Equal(blob);
        }
        finally
        {
            if (Directory.Exists(dir.FullName))
                Directory.Delete(dir.FullName, recursive: true);
        }
    }

    [Fact]
    public void Arena_MissingExpert_DoesNotInventADiskRead()
    {
        var arena = new RamArena();
        arena.Seal();
        var act = () => arena.Get(new ExpertKey(MosaicFamily.MimoV26Flash, 0, 0, ExpertRole.Gate));
        act.Should().Throw<KeyNotFoundException>().WithMessage("*will not page*");
    }

    [Fact]
    public void Cache_PromotesUntilCapacity_ThenEvictsCold()
    {
        var cache = new VramExpertCache(capacityBytes: 40);
        var a = new ExpertKey(MosaicFamily.MimoV26Flash, 1, 0, ExpertRole.Down);
        var b = new ExpertKey(MosaicFamily.MimoV26Flash, 1, 1, ExpertRole.Down);
        var c = new ExpertKey(MosaicFamily.MimoV26Flash, 1, 2, ExpertRole.Down);
        cache.Promote(a, new byte[20]);
        cache.Promote(b, new byte[20]);
        cache.TryGet(a, out _).Should().BeTrue();
        cache.Promote(c, new byte[20]);
        cache.TryGet(a, out _).Should().BeTrue("recent hits keep A");
        cache.TryGet(b, out _).Should().BeFalse("B is colder and should be evicted");
        cache.TryGet(c, out _).Should().BeTrue();
    }

    [Fact]
    public void Doorbell_HitIsGpu_MissIsDma_SaturatedIsCpu_NeverTouchesDisk()
    {
        var adapter = new MimoV26FlashAdapter();
        var arena = new RamArena();
        var cache = new VramExpertCache(10_000);
        foreach (var expert in new[] { 0, 1, 2 })
        {
            var blob = ExpertPacker.Pack(adapter, 1, expert, ExpertRole.Down, new float[8], 2, 4);
            arena.Pin(ExpertPacker.Unpack(blob), blob);
        }
        arena.Seal();

        var door = new Doorbell(8);
        door.Write([0]);
        var first = door.Dispatch(MosaicFamily.MimoV26Flash, 1, arena, cache);
        first.Should().ContainSingle().Which.Kind.Should().Be(ExpertDispatchKind.RamDma);

        door.Write([0, 1]);
        var second = door.Dispatch(MosaicFamily.MimoV26Flash, 1, arena, cache);
        second.Select(d => d.Kind).Should().Equal(ExpertDispatchKind.GpuCached, ExpertDispatchKind.RamDma);

        door.Write([2]);
        var cpu = door.Dispatch(MosaicFamily.MimoV26Flash, 1, arena, cache, dmaSaturated: true);
        cpu.Should().ContainSingle().Which.Kind.Should().Be(ExpertDispatchKind.CpuInPlace);

        var act = () =>
        {
            door.Write([9]);
            door.Dispatch(MosaicFamily.MimoV26Flash, 1, arena, cache);
        };
        act.Should().Throw<InvalidOperationException>().WithMessage("*Refusing to touch disk*");
    }
}
