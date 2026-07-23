using System.Buffers.Binary;
using StackScope.Core.Transactions;

namespace StackScope.Core.Storage;

/// <summary>
/// Binary layout of an event record on disk. Fixed-size prefix + variable
/// payload + variable marker table + variable transaction id.
///
/// <code>
/// | u32 record_length          |     4 bytes
/// | u64 event_id               |     8
/// | i64 timestamp_ns           |     8
/// | u8  kind                   |     1
/// | i32 token_index            |     4
/// | i32 layer_index            |     4
/// | i32 head_index             |     4
/// | i32 thread_id              |     4
/// | i32 stream_id              |     4
/// | i32 device_id              |     4
/// | u16 txid_length            |     2
/// | u32 payload_length         |     4
/// | u16 marker_count           |     2
/// | bytes txid (utf-8)         |     txid_length
/// | bytes payload              |     payload_length
/// | markers[]                  |     marker_count × sizeof(marker record)
/// </code>
///
/// Marker record (fixed size on-disk; name is stored in a per-transaction
/// string table separately by <see cref="EventStore"/>):
/// <code>
/// | u32 name_id       |
/// | i64 begin_ns      |
/// | i64 end_ns        |
/// | u32 color_rgba    |
/// | i32 thread_id     |
/// | i32 stream_id     |
/// | u64 correlation_id|
/// </code>
/// Total marker size: 4+8+8+4+4+4+8 = 40 bytes.
/// </summary>
public static class EventRecord
{
    public const int MarkerSize = 40;
    private const int FixedHeader = 4  // record_length
                                  + 8  // event_id
                                  + 8  // ts
                                  + 1  // kind
                                  + 4  // token
                                  + 4  // layer
                                  + 4  // head
                                  + 4  // thread
                                  + 4  // stream
                                  + 4  // device
                                  + 2  // txid_length
                                  + 4  // payload_length
                                  + 2; // marker_count

    /// <summary>Total byte size of a serialised event.</summary>
    public static int SizeOf(int txidUtf8Length, int payloadLength, int markerCount)
        => FixedHeader + txidUtf8Length + payloadLength + markerCount * MarkerSize;

    public static int Write(
        Span<byte> dst,
        TransactionEvent e,
        ReadOnlySpan<byte> txidUtf8,
        Func<string, uint> nameToId)
    {
        int totalLen = SizeOf(txidUtf8.Length, e.Payload.Length, e.Markers.Count);
        if (dst.Length < totalLen)
            throw new ArgumentException("Destination buffer too small.", nameof(dst));

        int o = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(dst[o..], (uint)totalLen); o += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(dst[o..], e.EventId);      o += 8;
        BinaryPrimitives.WriteInt64LittleEndian(dst[o..], e.TimestampNs);   o += 8;
        dst[o++] = (byte)e.Kind;
        BinaryPrimitives.WriteInt32LittleEndian(dst[o..], e.TokenIndex);    o += 4;
        BinaryPrimitives.WriteInt32LittleEndian(dst[o..], e.LayerIndex);    o += 4;
        BinaryPrimitives.WriteInt32LittleEndian(dst[o..], e.HeadIndex);     o += 4;
        BinaryPrimitives.WriteInt32LittleEndian(dst[o..], e.ThreadId);      o += 4;
        BinaryPrimitives.WriteInt32LittleEndian(dst[o..], e.StreamId);      o += 4;
        BinaryPrimitives.WriteInt32LittleEndian(dst[o..], e.DeviceId);      o += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(dst[o..], (ushort)txidUtf8.Length); o += 2;
        BinaryPrimitives.WriteUInt32LittleEndian(dst[o..], (uint)e.Payload.Length); o += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(dst[o..], (ushort)e.Markers.Count); o += 2;

        txidUtf8.CopyTo(dst[o..]); o += txidUtf8.Length;
        e.Payload.Span.CopyTo(dst[o..]); o += e.Payload.Length;

        for (int i = 0; i < e.Markers.Count; i++)
        {
            var m = e.Markers[i];
            uint nameId = nameToId(m.Name);
            BinaryPrimitives.WriteUInt32LittleEndian(dst[o..], nameId);        o += 4;
            BinaryPrimitives.WriteInt64LittleEndian(dst[o..], m.BeginNs);      o += 8;
            BinaryPrimitives.WriteInt64LittleEndian(dst[o..], m.EndNs);        o += 8;
            BinaryPrimitives.WriteUInt32LittleEndian(dst[o..], m.ColorRgba);   o += 4;
            BinaryPrimitives.WriteInt32LittleEndian(dst[o..], m.ThreadId);     o += 4;
            BinaryPrimitives.WriteInt32LittleEndian(dst[o..], m.StreamId);     o += 4;
            BinaryPrimitives.WriteUInt64LittleEndian(dst[o..], m.CorrelationId); o += 8;
        }

        return totalLen;
    }

    public static TransactionEvent Read(ReadOnlySpan<byte> src, Func<uint, string> idToName)
    {
        int o = 0;
        uint totalLen = BinaryPrimitives.ReadUInt32LittleEndian(src[o..]); o += 4;
        if (totalLen > src.Length)
            throw new InvalidDataException("Truncated event record.");

        ulong eventId   = BinaryPrimitives.ReadUInt64LittleEndian(src[o..]); o += 8;
        long  tsNs      = BinaryPrimitives.ReadInt64LittleEndian(src[o..]);  o += 8;
        var   kind      = (EventKind)src[o++];
        int   token     = BinaryPrimitives.ReadInt32LittleEndian(src[o..]);  o += 4;
        int   layer     = BinaryPrimitives.ReadInt32LittleEndian(src[o..]);  o += 4;
        int   head      = BinaryPrimitives.ReadInt32LittleEndian(src[o..]);  o += 4;
        int   thread    = BinaryPrimitives.ReadInt32LittleEndian(src[o..]);  o += 4;
        int   stream    = BinaryPrimitives.ReadInt32LittleEndian(src[o..]);  o += 4;
        int   device    = BinaryPrimitives.ReadInt32LittleEndian(src[o..]);  o += 4;
        int   txidLen   = BinaryPrimitives.ReadUInt16LittleEndian(src[o..]); o += 2;
        int   payloadLen= (int)BinaryPrimitives.ReadUInt32LittleEndian(src[o..]); o += 4;
        int   markerCt  = BinaryPrimitives.ReadUInt16LittleEndian(src[o..]); o += 2;

        string txid = System.Text.Encoding.UTF8.GetString(src.Slice(o, txidLen));
        o += txidLen;
        var payload = src.Slice(o, payloadLen).ToArray();
        o += payloadLen;

        var markers = new TraceMarker[markerCt];
        for (int i = 0; i < markerCt; i++)
        {
            uint nameId = BinaryPrimitives.ReadUInt32LittleEndian(src[o..]); o += 4;
            long beg    = BinaryPrimitives.ReadInt64LittleEndian(src[o..]);  o += 8;
            long end    = BinaryPrimitives.ReadInt64LittleEndian(src[o..]);  o += 8;
            uint color  = BinaryPrimitives.ReadUInt32LittleEndian(src[o..]); o += 4;
            int  mtid   = BinaryPrimitives.ReadInt32LittleEndian(src[o..]);  o += 4;
            int  msid   = BinaryPrimitives.ReadInt32LittleEndian(src[o..]);  o += 4;
            ulong cid   = BinaryPrimitives.ReadUInt64LittleEndian(src[o..]); o += 8;
            markers[i] = new TraceMarker(idToName(nameId), beg, end, color, mtid, msid, cid);
        }

        return new TransactionEvent(
            eventId, txid, tsNs, kind,
            token, layer, head, thread, stream, device,
            payload, markers);
    }
}
