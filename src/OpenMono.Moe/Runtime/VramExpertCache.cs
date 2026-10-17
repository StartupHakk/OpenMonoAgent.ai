namespace OpenMono.Moe.Runtime;

/// <summary>
/// Adaptive hot-expert cache that occupies spare VRAM after trunk + KV.
/// This scaffold stores pinned blobs in process memory and tracks hits the
/// same way the GPU cache will. Eviction is recency-weighted LFU.
/// </summary>
public sealed class VramExpertCache
{
    private readonly record struct Slot(byte[] Blob, int Hits, long LastTick);

    private readonly Dictionary<ExpertKey, Slot> _slots = [];
    private readonly object _gate = new();
    private long _tick;

    public VramExpertCache(long capacityBytes)
    {
        if (capacityBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacityBytes));
        CapacityBytes = capacityBytes;
    }

    public long CapacityBytes { get; }
    public long BytesUsed { get; private set; }
    public int Count { get; private set; }
    public int Hits { get; private set; }
    public int Misses { get; private set; }

    public bool TryGet(ExpertKey key, out ReadOnlyMemory<byte> blob)
    {
        lock (_gate)
        {
            if (_slots.TryGetValue(key, out var slot))
            {
                _slots[key] = slot with { Hits = slot.Hits + 1, LastTick = ++_tick };
                Hits++;
                blob = slot.Blob;
                return true;
            }
            Misses++;
            blob = ReadOnlyMemory<byte>.Empty;
            return false;
        }
    }

    public void Promote(ExpertKey key, ReadOnlySpan<byte> source)
    {
        lock (_gate)
        {
            if (_slots.ContainsKey(key))
                return;
            long need = source.Length;
            if (need > CapacityBytes)
                return; // expert larger than the whole cache; stay in RAM
            while (BytesUsed + need > CapacityBytes && _slots.Count > 0)
                EvictColdest();
            var owned = source.ToArray();
            _slots[key] = new Slot(owned, Hits: 1, LastTick: ++_tick);
            BytesUsed += owned.Length;
            Count = _slots.Count;
        }
    }

    public IReadOnlyList<ExpertKey> SnapshotKeys()
    {
        lock (_gate)
            return _slots.Keys.ToArray();
    }

    private void EvictColdest()
    {
        ExpertKey coldest = default;
        double worst = double.MaxValue;
        bool any = false;
        foreach (var (key, slot) in _slots)
        {
            // Recency-weighted LFU: hits / age. Lower is colder.
            double age = Math.Max(1, _tick - slot.LastTick);
            double score = slot.Hits / age;
            if (!any || score < worst)
            {
                worst = score;
                coldest = key;
                any = true;
            }
        }
        if (!any)
            return;
        BytesUsed -= _slots[coldest].Blob.Length;
        _slots.Remove(coldest);
        Count = _slots.Count;
    }
}
