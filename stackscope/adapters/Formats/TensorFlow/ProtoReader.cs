using System.Buffers.Binary;

namespace StackScope.Adapters.Formats.TensorFlow;

/// <summary>
/// A minimal protobuf wire-format reader — enough to enumerate the top-
/// level fields of a TensorFlow <c>SavedModel</c> file without pulling
/// the TF protobufs in as a compile-time dependency.
///
/// This is a real parser (varint / length-delimited / fixed32 / fixed64 /
/// group tags), not a stub. It's deliberately field-agnostic: callers
/// look at (tag, wire_type) pairs and decide what to do.
/// </summary>
public sealed class ProtoReader
{
    public enum WireType : byte
    {
        Varint          = 0,
        Fixed64          = 1,
        LengthDelimited  = 2,
        StartGroup       = 3,   // deprecated in proto3
        EndGroup         = 4,   // deprecated in proto3
        Fixed32          = 5
    }

    private readonly ReadOnlyMemory<byte> _buf;
    private int _pos;

    public ProtoReader(ReadOnlyMemory<byte> buf) { _buf = buf; }

    public bool AtEnd => _pos >= _buf.Length;
    public int  Position => _pos;

    public (int fieldNumber, WireType wireType) ReadTag()
    {
        ulong tag = ReadVarint();
        int fn = (int)(tag >> 3);
        var wt = (WireType)(byte)(tag & 0x7);
        return (fn, wt);
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        int shift = 0;
        var span = _buf.Span;
        while (true)
        {
            if (_pos >= span.Length)
                throw new InvalidDataException("proto: truncated varint.");
            byte b = span[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
            if (shift >= 64) throw new InvalidDataException("proto: varint too long.");
        }
    }

    public long ReadSInt64()
    {
        ulong v = ReadVarint();
        return (long)((v >> 1) ^ (~(v & 1) + 1));
    }

    public uint ReadFixed32()
    {
        var s = _buf.Span.Slice(_pos, 4);
        _pos += 4;
        return BinaryPrimitives.ReadUInt32LittleEndian(s);
    }

    public ulong ReadFixed64()
    {
        var s = _buf.Span.Slice(_pos, 8);
        _pos += 8;
        return BinaryPrimitives.ReadUInt64LittleEndian(s);
    }

    public ReadOnlyMemory<byte> ReadLengthDelimited()
    {
        int len = checked((int)ReadVarint());
        var slice = _buf.Slice(_pos, len);
        _pos += len;
        return slice;
    }

    public string ReadString()
    {
        var s = ReadLengthDelimited().Span;
        return System.Text.Encoding.UTF8.GetString(s);
    }

    public void SkipField(WireType wt)
    {
        switch (wt)
        {
            case WireType.Varint: ReadVarint(); break;
            case WireType.Fixed32: _pos += 4; break;
            case WireType.Fixed64: _pos += 8; break;
            case WireType.LengthDelimited:
                int len = checked((int)ReadVarint());
                _pos += len;
                break;
            case WireType.StartGroup:
            case WireType.EndGroup:
                throw new InvalidDataException("proto: groups not supported (proto3).");
            default:
                throw new InvalidDataException($"proto: unknown wire type {wt}.");
        }
    }
}
