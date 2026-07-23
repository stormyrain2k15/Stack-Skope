using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

namespace StackScope.Adapters.Drivers.Vulkan;

/// <summary>
/// Out-of-band bridge between the Vulkan-side workers (llama.cpp Vulkan
/// backend, or a Vulkan-capable Python worker via Kompute/vkFFT) and the
/// <see cref="VulkanCaptureBackend"/>.
///
/// Layout (per-transaction, named "stackscope.vk.{txid}"):
///
///   [ u32 magic 0x56534B53 = 'SKSV' ]
///   [ u32 version = 1                       ]
///   [ u64 write_cursor (atomic)             ]
///   [ u64 read_cursor  (atomic)             ]
///   [ ring buffer of QueryRecord[N]         ]
///
/// QueryRecord (64 bytes, packed):
///   [ i64 begin_ns ][ i64 end_ns ]
///   [ u64 correlation_id ]
///   [ i32 thread_id ][ i32 queue_family ]
///   [ i32 device_id ][ i32 label_len ]
///   [ i32 pipeline_len ][ i32 reserved ]
///   [ 24 bytes inline strings (label,pipeline; truncated if longer) ]
/// </summary>
public sealed class VulkanCaptureBridge : IDisposable
{
    public sealed record QueryRecord(
        long BeginNs, long EndNs, ulong CorrelationId,
        int ThreadId, int QueueFamily, int DeviceId,
        string? Label, string? PipelineName);

    private const uint Magic       = 0x56534B53;
    private const int  RecordSize  = 64;
    private const int  RingCount   = 4096;
    private const int  HeaderSize  = 4 + 4 + 8 + 8;
    private const int  Capacity    = HeaderSize + RingCount * RecordSize;

    private MemoryMappedFile _mmf;
    private MemoryMappedViewAccessor _view;

    private VulkanCaptureBridge(MemoryMappedFile mmf, MemoryMappedViewAccessor v)
    {
        _mmf = mmf; _view = v;
    }

    public static VulkanCaptureBridge? OpenOrNull(string transactionId)
    {
        string name = "stackscope.vk." + transactionId;
        try
        {
            var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
            var v = mmf.CreateViewAccessor(0, Capacity, MemoryMappedFileAccess.ReadWrite);
            uint magic = v.ReadUInt32(0);
            if (magic != Magic) { v.Dispose(); mmf.Dispose(); return null; }
            return new VulkanCaptureBridge(mmf, v);
        }
        catch (FileNotFoundException) { return null; }
        catch (PlatformNotSupportedException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public IReadOnlyList<QueryRecord> TryReadBatch()
    {
        ulong w = _view.ReadUInt64(8);
        ulong r = _view.ReadUInt64(16);
        if (r == w) return Array.Empty<QueryRecord>();

        var list = new List<QueryRecord>();
        while (r != w)
        {
            long recOff = HeaderSize + (long)(r % (ulong)RingCount) * RecordSize;
            byte[] rec = new byte[RecordSize];
            _view.ReadArray(recOff, rec, 0, RecordSize);

            long beg  = BinaryPrimitives.ReadInt64LittleEndian(rec.AsSpan(0));
            long end  = BinaryPrimitives.ReadInt64LittleEndian(rec.AsSpan(8));
            ulong cid = BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(16));
            int  tid  = BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(24));
            int  qf   = BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(28));
            int  did  = BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(32));
            int  ll   = BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(36));
            int  pl   = BinaryPrimitives.ReadInt32LittleEndian(rec.AsSpan(40));

            var strs = rec.AsSpan(48, 24);
            int lclamped = Math.Clamp(ll, 0, 24);
            int pclamped = Math.Clamp(pl, 0, Math.Max(0, 24 - lclamped));
            string? label = lclamped == 0 ? null
                : System.Text.Encoding.UTF8.GetString(strs[..lclamped]);
            string? pipe  = pclamped == 0 ? null
                : System.Text.Encoding.UTF8.GetString(strs.Slice(lclamped, pclamped));

            list.Add(new QueryRecord(beg, end, cid, tid, qf, did, label, pipe));
            r++;
        }
        _view.Write(16, r);
        return list;
    }

    public void Dispose()
    {
        _view.Dispose();
        _mmf.Dispose();
    }
}
