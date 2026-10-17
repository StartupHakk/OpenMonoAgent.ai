using OpenMono.Moe.Family;
using OpenMono.Moe.Quant;

namespace OpenMono.Moe.Runtime;

/// <summary>
/// Pinned host-memory expert store. After <see cref="Pin"/>, <see cref="Get"/>
/// only reads the owned buffers. There is no mmap path and no file handle
/// kept alive for decode.
/// </summary>
public sealed class RamArena
{
    private readonly Dictionary<ExpertKey, byte[]> _pinned = [];
    private bool _sealed;

    public int ExpertCount => _pinned.Count;
    public long BytesPinned { get; private set; }
    public bool IsSealed => _sealed;

    public void Pin(PackedExpert expert, byte[] blob)
    {
        if (_sealed)
            throw new InvalidOperationException("Arena is sealed; startup load is finished.");
        var key = ExpertKey.From(expert);
        if (_pinned.ContainsKey(key))
            throw new InvalidOperationException($"Expert {key} is already pinned.");
        // Copy so the caller can drop the source buffer (file, download, ...).
        var owned = new byte[blob.Length];
        Buffer.BlockCopy(blob, 0, owned, 0, blob.Length);
        _pinned[key] = owned;
        BytesPinned += owned.Length;
    }

    /// <summary>
    /// Startup-only disk ingest. Reads each file fully, pins the bytes, and
    /// closes the handle before returning. Decode must never call this.
    /// </summary>
    public void PinFromDisk(IEnumerable<string> packFiles)
    {
        if (_sealed)
            throw new InvalidOperationException("Arena is sealed; startup load is finished.");
        foreach (var path in packFiles)
        {
            var blob = File.ReadAllBytes(path);
            var expert = ExpertPacker.Unpack(blob);
            Pin(expert, blob);
        }
    }

    public void Seal() => _sealed = true;

    public bool TryGet(ExpertKey key, out ReadOnlyMemory<byte> blob)
    {
        if (_pinned.TryGetValue(key, out var owned))
        {
            blob = owned;
            return true;
        }
        blob = ReadOnlyMemory<byte>.Empty;
        return false;
    }

    public ReadOnlyMemory<byte> Get(ExpertKey key)
    {
        if (!TryGet(key, out var blob))
            throw new KeyNotFoundException($"Expert {key} is not in the RAM arena. Mosaic will not page it from disk.");
        return blob;
    }

    public IReadOnlyCollection<ExpertKey> Keys => _pinned.Keys;
}

public readonly record struct ExpertKey(MosaicFamily Family, int Layer, int Expert, ExpertRole Role)
{
    public static ExpertKey From(PackedExpert e) => new(e.Family, e.Layer, e.Expert, e.Role);
    public override string ToString() => $"{Family}:L{Layer}:E{Expert}:{Role}";
}
