using System.Buffers.Binary;
using System.Text;
using StackScope.Core.Models;

namespace StackScope.Adapters.Formats.Gguf;

/// <summary>
/// Real GGUF v2/v3 reader. See github.com/ggerganov/ggml gguf.md.
/// Layout (little-endian throughout):
///   [ magic "GGUF" ][ u32 version ][ u64 tensor_count ][ u64 kv_count ]
///   [ kv_count × KV pair ][ tensor_count × tensor info ]
///   [ alignment padding to (alignment) bytes, default 32 ][ tensor data ]
///
/// A KV pair is: [ string key ][ u32 value_type ][ value ] where a string
/// is [ u64 length ][ length bytes utf-8 ] and value type follows
/// <see cref="GgufValueType"/>. Array values recurse with a nested type.
/// </summary>
public sealed class GgufReader
{
    private const uint MagicGguf = 0x46554747; // 'G','G','U','F' little-endian

    public sealed record TensorHeader(
        string Name,
        long[] Shape,
        GgmlType GgmlType,
        long FileByteOffset);

    public sealed record GgufFile(
        uint Version,
        ulong Alignment,
        IReadOnlyDictionary<string, object> Metadata,
        IReadOnlyList<TensorHeader> Tensors);

    private readonly BinaryReader _br;
    private readonly Stream _s;

    public GgufReader(Stream stream)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("Stream must be seekable.", nameof(stream));
        _s  = stream;
        _br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
    }

    public static GgufFile ReadFile(string path)
    {
        using var fs = File.OpenRead(path);
        var r = new GgufReader(fs);
        return r.Read();
    }

    public GgufFile Read()
    {
        _s.Position = 0;
        uint magic = _br.ReadUInt32();
        if (magic != MagicGguf)
            throw new InvalidDataException(
                $"gguf: bad magic 0x{magic:X8}, expected 0x{MagicGguf:X8}.");
        uint version = _br.ReadUInt32();
        if (version < 2 || version > 3)
            throw new InvalidDataException(
                $"gguf: unsupported version {version}, expected 2 or 3.");

        ulong tensorCount = _br.ReadUInt64();
        ulong kvCount     = _br.ReadUInt64();

        var metadata = new Dictionary<string, object>(
            capacity: (int)Math.Min(kvCount, int.MaxValue),
            comparer: StringComparer.Ordinal);

        for (ulong i = 0; i < kvCount; i++)
        {
            string key = ReadString();
            var vt = (GgufValueType)_br.ReadUInt32();
            metadata[key] = ReadValue(vt);
        }

        ulong alignment = metadata.TryGetValue("general.alignment", out var a)
            ? Convert.ToUInt64(a) : 32UL;

        // Tensor info section.
        var tensorHeaders = new List<(string name, long[] shape, GgmlType type, ulong relOffset)>(
            (int)Math.Min(tensorCount, int.MaxValue));
        for (ulong i = 0; i < tensorCount; i++)
        {
            string name = ReadString();
            uint nDims  = _br.ReadUInt32();
            if (nDims > 8) throw new InvalidDataException($"gguf: implausible ndims {nDims}.");
            var shape = new long[nDims];
            for (int d = 0; d < nDims; d++) shape[d] = (long)_br.ReadUInt64();
            var ttype  = (GgmlType)_br.ReadUInt32();
            ulong relOffset = _br.ReadUInt64();
            tensorHeaders.Add((name, shape, ttype, relOffset));
        }

        // Align to `alignment` for the tensor-data section.
        long dataStart = _s.Position;
        long pad = (long)alignment - (dataStart % (long)alignment);
        if (pad != (long)alignment) dataStart += pad;

        var tensors = new List<TensorHeader>(tensorHeaders.Count);
        foreach (var t in tensorHeaders)
        {
            tensors.Add(new TensorHeader(
                Name: t.name,
                Shape: t.shape,
                GgmlType: t.type,
                FileByteOffset: dataStart + (long)t.relOffset));
        }

        return new GgufFile(version, alignment, metadata, tensors);
    }

    private string ReadString()
    {
        ulong len = _br.ReadUInt64();
        if (len > 16 * 1024 * 1024)
            throw new InvalidDataException($"gguf: implausible string length {len}.");
        var buf = _br.ReadBytes((int)len);
        if (buf.Length != (int)len)
            throw new EndOfStreamException("gguf: truncated string.");
        return Encoding.UTF8.GetString(buf);
    }

    private object ReadValue(GgufValueType vt)
    {
        switch (vt)
        {
            case GgufValueType.Uint8:   return _br.ReadByte();
            case GgufValueType.Int8:    return _br.ReadSByte();
            case GgufValueType.Uint16:  return _br.ReadUInt16();
            case GgufValueType.Int16:   return _br.ReadInt16();
            case GgufValueType.Uint32:  return _br.ReadUInt32();
            case GgufValueType.Int32:   return _br.ReadInt32();
            case GgufValueType.Float32: return _br.ReadSingle();
            case GgufValueType.Bool:    return _br.ReadByte() != 0;
            case GgufValueType.String:  return ReadString();
            case GgufValueType.Uint64:  return _br.ReadUInt64();
            case GgufValueType.Int64:   return _br.ReadInt64();
            case GgufValueType.Float64: return _br.ReadDouble();
            case GgufValueType.Array:
            {
                var innerType = (GgufValueType)_br.ReadUInt32();
                ulong count = _br.ReadUInt64();
                if (count > (ulong)int.MaxValue)
                    throw new InvalidDataException($"gguf: array length {count} too large.");
                var arr = new object[count];
                for (ulong i = 0; i < count; i++) arr[i] = ReadValue(innerType);
                return arr;
            }
            default:
                throw new InvalidDataException($"gguf: unknown value type {(uint)vt}.");
        }
    }

    /// <summary>Compute the on-disk byte length of a tensor given its type and shape.</summary>
    public static long ComputeTensorByteLength(GgmlType type, long[] shape)
    {
        long n = 1;
        for (int i = 0; i < shape.Length; i++) n *= shape[i];
        return type switch
        {
            GgmlType.F32     => n * 4,
            GgmlType.F16     => n * 2,
            GgmlType.BF16    => n * 2,
            GgmlType.F64     => n * 8,
            GgmlType.I8      => n * 1,
            GgmlType.I16     => n * 2,
            GgmlType.I32     => n * 4,
            GgmlType.I64     => n * 8,

            // Block-quantised layouts. Row length must be divisible by block size.
            GgmlType.Q4_0    => BlockBytes(n, blockElts: 32, blockBytes: 18),   //  2 (d) + 16 (qs)
            GgmlType.Q4_1    => BlockBytes(n, blockElts: 32, blockBytes: 20),   //  2+2+16
            GgmlType.Q5_0    => BlockBytes(n, blockElts: 32, blockBytes: 22),   //  2+4+16
            GgmlType.Q5_1    => BlockBytes(n, blockElts: 32, blockBytes: 24),   //  2+2+4+16
            GgmlType.Q8_0    => BlockBytes(n, blockElts: 32, blockBytes: 34),   //  2 + 32
            GgmlType.Q8_1    => BlockBytes(n, blockElts: 32, blockBytes: 36),   //  4 + 32
            GgmlType.Q2_K    => BlockBytes(n, blockElts: 256, blockBytes: 84),
            GgmlType.Q3_K    => BlockBytes(n, blockElts: 256, blockBytes: 110),
            GgmlType.Q4_K    => BlockBytes(n, blockElts: 256, blockBytes: 144),
            GgmlType.Q5_K    => BlockBytes(n, blockElts: 256, blockBytes: 176),
            GgmlType.Q6_K    => BlockBytes(n, blockElts: 256, blockBytes: 210),
            GgmlType.Q8_K    => BlockBytes(n, blockElts: 256, blockBytes: 292),
            GgmlType.IQ2_XXS => BlockBytes(n, blockElts: 256, blockBytes: 66),
            GgmlType.IQ2_XS  => BlockBytes(n, blockElts: 256, blockBytes: 74),
            GgmlType.IQ3_XXS => BlockBytes(n, blockElts: 256, blockBytes: 98),
            GgmlType.IQ1_S   => BlockBytes(n, blockElts: 256, blockBytes: 50),
            GgmlType.IQ4_NL  => BlockBytes(n, blockElts: 32,  blockBytes: 18),
            GgmlType.IQ4_XS  => BlockBytes(n, blockElts: 256, blockBytes: 136),
            GgmlType.IQ3_S   => BlockBytes(n, blockElts: 256, blockBytes: 110),
            GgmlType.IQ2_S   => BlockBytes(n, blockElts: 256, blockBytes: 82),
            GgmlType.IQ1_M   => BlockBytes(n, blockElts: 256, blockBytes: 56),
            _ => throw new InvalidDataException($"gguf: unsupported tensor type {type}.")
        };
    }

    private static long BlockBytes(long elements, int blockElts, int blockBytes)
    {
        if (elements % blockElts != 0)
            throw new InvalidDataException(
                $"gguf: element count {elements} not divisible by block size {blockElts}.");
        return elements / blockElts * blockBytes;
    }

    /// <summary>Enumerate tensors as core <see cref="TensorInfo"/> values.</summary>
    public static IReadOnlyList<TensorInfo> ReadTensorInventory(string path)
    {
        var f = ReadFile(path);
        var list = new List<TensorInfo>(f.Tensors.Count);
        foreach (var t in f.Tensors)
        {
            var (dtype, quant) = QuantOf(t.GgmlType);
            long byteLength = ComputeTensorByteLength(t.GgmlType, t.Shape);
            list.Add(new TensorInfo(
                Name: t.Name,
                Shape: t.Shape,
                DType: dtype,
                Quantization: quant,
                ByteOffset: t.FileByteOffset,
                ByteLength: byteLength,
                SourceFile: path,
                Sha256: null));
        }
        return list;
    }

    private static (string dtype, QuantizationInfo quant) QuantOf(GgmlType t) => t switch
    {
        GgmlType.F32     => ("f32",  QuantizationInfo.Fp32),
        GgmlType.F16     => ("f16",  QuantizationInfo.Fp16),
        GgmlType.BF16    => ("bf16", QuantizationInfo.Bf16),
        GgmlType.F64     => ("f64",  new(QuantizationScheme.F64, 1, 64, false, false)),
        GgmlType.I8      => ("i8",   new(QuantizationScheme.I8, 1, 8, false, false)),
        GgmlType.Q4_0    => ("q4_0", new(QuantizationScheme.Q4_0, 32, 4, true, false)),
        GgmlType.Q4_1    => ("q4_1", new(QuantizationScheme.Q4_1, 32, 4, true, true)),
        GgmlType.Q5_0    => ("q5_0", new(QuantizationScheme.Q5_0, 32, 5, true, false)),
        GgmlType.Q5_1    => ("q5_1", new(QuantizationScheme.Q5_1, 32, 5, true, true)),
        GgmlType.Q8_0    => ("q8_0", new(QuantizationScheme.Q8_0, 32, 8, true, false)),
        GgmlType.Q2_K    => ("q2_k", new(QuantizationScheme.Q2_K, 256, 2, true, true)),
        GgmlType.Q3_K    => ("q3_k", new(QuantizationScheme.Q3_K, 256, 3, true, true)),
        GgmlType.Q4_K    => ("q4_k", new(QuantizationScheme.Q4_K, 256, 4, true, true)),
        GgmlType.Q5_K    => ("q5_k", new(QuantizationScheme.Q5_K, 256, 5, true, true)),
        GgmlType.Q6_K    => ("q6_k", new(QuantizationScheme.Q6_K, 256, 6, true, true)),
        GgmlType.IQ2_XXS => ("iq2_xxs", new(QuantizationScheme.IQ2_XXS, 256, 2, true, false)),
        GgmlType.IQ2_XS  => ("iq2_xs",  new(QuantizationScheme.IQ2_XS,  256, 2, true, false)),
        GgmlType.IQ3_XXS => ("iq3_xxs", new(QuantizationScheme.IQ3_XXS, 256, 3, true, false)),
        GgmlType.IQ1_S   => ("iq1_s",   new(QuantizationScheme.IQ1_S,   256, 1, true, false)),
        _                => (t.ToString().ToLowerInvariant(),
                             new(QuantizationScheme.None, 1, 0, false, false))
    };
}
