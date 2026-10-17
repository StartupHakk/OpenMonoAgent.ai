using System.Buffers.Binary;
using System.Text;
using OpenMono.Moe.Family;

namespace OpenMono.Moe.Quant;

/// <summary>
/// OMS1 expert packer. Writes self-describing blobs the RAM arena can pin
/// without a second conversion. Startup I/O only.
/// </summary>
public static class ExpertPacker
{
    public static readonly byte[] Magic = "OMS1"u8.ToArray();
    public const ushort Version = 1;

    public static byte[] Pack(
        IModelFamilyAdapter adapter,
        int layer,
        int expert,
        ExpertRole role,
        float[] weights,
        int rows,
        int cols)
    {
        if (weights.Length != checked(rows * cols))
            throw new ArgumentException("rows * cols must equal weights.Length.");
        if (!adapter.OwnsRuntime)
            throw new NotSupportedException($"{adapter.DisplayName} is not packed by Mosaic.");

        int bpwTenths = adapter.BitsPerWeightTenths(role, layer);
        int bits = Math.Max(2, (bpwTenths + 5) / 10);
        var tensor = BlockQuantizer.Quantize(weights, bits);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);
        w.Write((ushort)adapter.Family);
        w.Write((ushort)layer);
        w.Write((ushort)expert);
        w.Write((byte)role);
        w.Write((byte)bpwTenths);
        w.Write((ushort)tensor.BlockSize);
        w.Write((uint)rows);
        w.Write((uint)cols);
        // payload: codes then fp32 scales (scaffold; GPU pack can switch to fp16)
        int payload = tensor.Codes.Length + tensor.Scales.Length * sizeof(float);
        w.Write((uint)payload);
        w.Write(tensor.Codes);
        var scaleBytes = new byte[tensor.Scales.Length * sizeof(float)];
        for (int i = 0; i < tensor.Scales.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(scaleBytes.AsSpan(i * 4), tensor.Scales[i]);
        w.Write(scaleBytes);
        w.Flush();
        return ms.ToArray();
    }

    public static PackedExpert Unpack(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 28)
            throw new InvalidDataException("OMS1 blob is truncated.");
        if (!blob[..4].SequenceEqual(Magic))
            throw new InvalidDataException("Not an OMS1 blob.");
        int o = 4;
        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(blob[o..]); o += 2;
        if (version != Version)
            throw new InvalidDataException($"Unsupported OMS1 version {version}.");
        var family = (MosaicFamily)BinaryPrimitives.ReadUInt16LittleEndian(blob[o..]); o += 2;
        int layer = BinaryPrimitives.ReadUInt16LittleEndian(blob[o..]); o += 2;
        int expert = BinaryPrimitives.ReadUInt16LittleEndian(blob[o..]); o += 2;
        var role = (ExpertRole)blob[o]; o += 1;
        int bpwTenths = blob[o]; o += 1;
        int block = BinaryPrimitives.ReadUInt16LittleEndian(blob[o..]); o += 2;
        int rows = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[o..]); o += 4;
        int cols = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[o..]); o += 4;
        int payload = (int)BinaryPrimitives.ReadUInt32LittleEndian(blob[o..]); o += 4;
        if (o + payload > blob.Length)
            throw new InvalidDataException("OMS1 payload overruns the blob.");
        int count = checked(rows * cols);
        var codes = blob.Slice(o, count).ToArray();
        o += count;
        int scaleCount = (payload - count) / sizeof(float);
        var scales = new float[scaleCount];
        for (int i = 0; i < scaleCount; i++)
        {
            scales[i] = BinaryPrimitives.ReadSingleLittleEndian(blob[(o + i * 4)..]);
        }
        int bits = Math.Max(2, (bpwTenths + 5) / 10);
        return new PackedExpert(
            family, layer, expert, role, bpwTenths,
            new PackedTensor(bits, block, count, codes, scales),
            rows, cols);
    }
}

public sealed record PackedExpert(
    MosaicFamily Family,
    int Layer,
    int Expert,
    ExpertRole Role,
    int BpwTenths,
    PackedTensor Tensor,
    int Rows,
    int Cols);
