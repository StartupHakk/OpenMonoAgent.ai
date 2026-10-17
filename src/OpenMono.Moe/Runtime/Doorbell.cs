using OpenMono.Moe.Family;
using OpenMono.Moe.Quant;

namespace OpenMono.Moe.Runtime;

public enum ExpertDispatchKind
{
    GpuCached,
    RamDma,
    CpuInPlace,
}

public readonly record struct ExpertDispatch(ExpertKey Key, ExpertDispatchKind Kind);

/// <summary>
/// Host-visible ring of expert ids chosen for the current token. The GPU
/// writes ids; the host turns them into cache hits (GPU), arena DMA, or
/// CPU-in-place. Never opens a file.
/// </summary>
public sealed class Doorbell
{
    private readonly int[] _ids;
    private int _count;

    public Doorbell(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _ids = new int[capacity];
    }

    public int Capacity => _ids.Length;
    public int Count => _count;

    public void Write(ReadOnlySpan<int> expertIds)
    {
        if (expertIds.Length > _ids.Length)
            throw new ArgumentException("Doorbell overflow: too many expert ids for this token.");
        expertIds.CopyTo(_ids);
        _count = expertIds.Length;
    }

    public ReadOnlySpan<int> Read() => _ids.AsSpan(0, _count);

    public IReadOnlyList<ExpertDispatch> Dispatch(
        MosaicFamily family,
        int layer,
        RamArena arena,
        VramExpertCache cache,
        ExpertRole role = ExpertRole.Down,
        bool dmaSaturated = false)
    {
        var result = new List<ExpertDispatch>(_count);
        foreach (var expert in Read())
        {
            var key = new ExpertKey(family, layer, expert, role);
            if (cache.TryGet(key, out _))
            {
                result.Add(new ExpertDispatch(key, ExpertDispatchKind.GpuCached));
                continue;
            }
            if (!arena.TryGet(key, out var blob))
                throw new InvalidOperationException($"Doorbell named {key} but the RAM arena does not have it. Refusing to touch disk.");
            if (dmaSaturated)
            {
                result.Add(new ExpertDispatch(key, ExpertDispatchKind.CpuInPlace));
                continue;
            }
            cache.Promote(key, blob.Span);
            result.Add(new ExpertDispatch(key, ExpertDispatchKind.RamDma));
        }
        return result;
    }
}
