using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using StackScope.Core.Models;

namespace StackScope.Adapters.Formats.SafeTensors;

/// <summary>
/// Real safetensors parser. See https://github.com/huggingface/safetensors
/// The on-disk layout is:
///   [ u64 little-endian header_size ][ header_size bytes of JSON ][ tensor bytes ... ]
///
/// The JSON header maps tensor names → { "dtype": ..., "shape": [...],
/// "data_offsets": [begin, end] } and may contain a "__metadata__" entry.
/// The offsets are relative to the start of the tensor byte area, i.e.
/// absolute_offset = 8 + header_size + begin.
/// </summary>
public sealed class SafeTensorsReader
{
    /// <summary>Header dictionary as parsed from the JSON prelude.</summary>
    public sealed record HeaderEntry(
        string DType,
        long[] Shape,
        long BeginOffset,       // relative to tensor area
        long EndOffset)
    {
        public long ByteLength => EndOffset - BeginOffset;
    }

    public sealed record SafeTensorsHeader(
        long HeaderByteLength,
        IReadOnlyDictionary<string, HeaderEntry> Tensors,
        IReadOnlyDictionary<string, string> Metadata);

    public static SafeTensorsHeader ReadHeader(Stream stream)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("Stream must be seekable.", nameof(stream));
        stream.Position = 0;

        Span<byte> lenBuf = stackalloc byte[8];
        int read = 0;
        while (read < 8)
        {
            int n = stream.Read(lenBuf[read..]);
            if (n == 0) throw new EndOfStreamException("safetensors: truncated header length.");
            read += n;
        }
        long headerLen = BinaryPrimitives.ReadInt64LittleEndian(lenBuf);
        if (headerLen <= 0 || headerLen > 100L * 1024 * 1024)
            throw new InvalidDataException($"safetensors: implausible header length {headerLen}.");

        byte[] json = new byte[headerLen];
        read = 0;
        while (read < headerLen)
        {
            int n = stream.Read(json, read, (int)(headerLen - read));
            if (n == 0) throw new EndOfStreamException("safetensors: truncated header body.");
            read += n;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tensors = new Dictionary<string, HeaderEntry>(StringComparer.Ordinal);
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name == "__metadata__")
            {
                foreach (var m in prop.Value.EnumerateObject())
                {
                    metadata[m.Name] = m.Value.ValueKind == JsonValueKind.String
                        ? m.Value.GetString() ?? ""
                        : m.Value.GetRawText();
                }
                continue;
            }

            var el = prop.Value;
            string dtype = el.GetProperty("dtype").GetString()
                ?? throw new InvalidDataException($"safetensors: tensor '{prop.Name}' missing dtype.");
            var shapeEl = el.GetProperty("shape");
            var shape = new long[shapeEl.GetArrayLength()];
            for (int i = 0; i < shape.Length; i++) shape[i] = shapeEl[i].GetInt64();

            var offEl = el.GetProperty("data_offsets");
            if (offEl.GetArrayLength() != 2)
                throw new InvalidDataException($"safetensors: tensor '{prop.Name}' bad data_offsets.");
            long a = offEl[0].GetInt64();
            long b = offEl[1].GetInt64();
            if (a < 0 || b < a) throw new InvalidDataException(
                $"safetensors: tensor '{prop.Name}' invalid range [{a},{b}).");

            tensors[prop.Name] = new HeaderEntry(dtype, shape, a, b);
        }

        return new SafeTensorsHeader(headerLen, tensors, metadata);
    }

    /// <summary>Enumerate tensors as core <see cref="TensorInfo"/> values.</summary>
    public static IReadOnlyList<TensorInfo> ReadTensorInventory(string path)
    {
        using var fs = File.OpenRead(path);
        var header = ReadHeader(fs);
        long tensorAreaStart = 8 + header.HeaderByteLength;

        var list = new List<TensorInfo>(header.Tensors.Count);
        foreach (var (name, h) in header.Tensors)
        {
            var quant = QuantOf(h.DType);
            list.Add(new TensorInfo(
                Name: name,
                Shape: h.Shape,
                DType: h.DType,
                Quantization: quant,
                ByteOffset: tensorAreaStart + h.BeginOffset,
                ByteLength: h.ByteLength,
                SourceFile: path,
                Sha256: null));
        }
        return list;
    }

    /// <summary>Compute SHA-256 of the entire file. Used for content hash of the descriptor.</summary>
    public static string ComputeFileSha256(string path)
    {
        using var fs = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>Validate that all tensor byte ranges fit within the file.</summary>
    public static void Validate(string path)
    {
        var fi = new FileInfo(path);
        using var fs = File.OpenRead(path);
        var header = ReadHeader(fs);
        long tensorAreaStart = 8 + header.HeaderByteLength;
        foreach (var (name, h) in header.Tensors)
        {
            long end = tensorAreaStart + h.EndOffset;
            if (end > fi.Length)
                throw new InvalidDataException(
                    $"safetensors: tensor '{name}' end offset {end} exceeds file size {fi.Length}.");
        }
    }

    private static QuantizationInfo QuantOf(string dtype)
    {
        return dtype switch
        {
            "F32" or "float32" => QuantizationInfo.Fp32,
            "F16" or "float16" => QuantizationInfo.Fp16,
            "BF16" or "bfloat16" => QuantizationInfo.Bf16,
            "F64" or "float64" => new(QuantizationScheme.F64, 1, 64, false, false),
            "I8"  or "int8"    => new(QuantizationScheme.I8, 1, 8, false, false),
            _ => new QuantizationInfo(QuantizationScheme.None, 1, BitsOfUnknown(dtype), false, false)
        };
    }

    private static long BitsOfUnknown(string dtype)
    {
        // Best-effort width parse: names look like "I32", "U16" etc.
        if (dtype.Length >= 2 && (dtype[0] == 'I' || dtype[0] == 'U' || dtype[0] == 'F'))
            if (long.TryParse(dtype[1..], out var bits) && bits > 0) return bits;
        return 0;
    }
}
