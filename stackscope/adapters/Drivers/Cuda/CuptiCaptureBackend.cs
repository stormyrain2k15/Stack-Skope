using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using StackScope.Core.Capture;
using StackScope.Core.Transactions;

namespace StackScope.Adapters.Drivers.Cuda;

/// <summary>
/// Real CUPTI activity+callback backend. Registers buffer callbacks with
/// CUPTI, parses kernel/memcpy/memory records into
/// <see cref="TransactionEvent"/> instances, and emits them into the
/// capture pipeline.
///
/// Windows-only at runtime. Compiles on any platform (the P/Invokes are
/// declared but not resolved until first call).
/// </summary>
public sealed class CuptiCaptureBackend : ICaptureBackend
{
    private readonly Channel<TransactionEvent> _out;
    private readonly int _deviceId;
    private string _transactionId = "";
    private ulong _nextEventId = 0;
    private static CuptiCaptureBackend? _instance;
    private CuptiInterop.BufferRequested? _reqDelegate;
    private CuptiInterop.BufferCompleted? _cmpDelegate;
    private bool _started;

    public string Kind => "driver.cuda";

    public CuptiCaptureBackend(int deviceId = 0)
    {
        _deviceId = deviceId;
        _out = Channel.CreateBounded<TransactionEvent>(
            new BoundedChannelOptions(capacity: 4096)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });
    }

    public Task StartAsync(string transactionId, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            _out.Writer.TryComplete();
            return Task.CompletedTask;
        }

        _transactionId = transactionId;
        _instance = this;
        _reqDelegate = OnBufferRequested;
        _cmpDelegate = OnBufferCompleted;

        Check(CuptiInterop.cuptiActivityRegisterCallbacks(_reqDelegate, _cmpDelegate));
        Check(CuptiInterop.cuptiActivityEnable(CuptiInterop.CUpti_ActivityKind.CONCURRENT_KERNEL));
        Check(CuptiInterop.cuptiActivityEnable(CuptiInterop.CUpti_ActivityKind.MEMCPY));
        Check(CuptiInterop.cuptiActivityEnable(CuptiInterop.CUpti_ActivityKind.MEMORY2));
        Check(CuptiInterop.cuptiActivityEnable(CuptiInterop.CUpti_ActivityKind.MARKER));
        _started = true;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        if (!_started) return Task.CompletedTask;
        try
        {
            CuptiInterop.cuptiActivityFlushAll(0);
            CuptiInterop.cuptiActivityDisable(CuptiInterop.CUpti_ActivityKind.CONCURRENT_KERNEL);
            CuptiInterop.cuptiActivityDisable(CuptiInterop.CUpti_ActivityKind.MEMCPY);
            CuptiInterop.cuptiActivityDisable(CuptiInterop.CUpti_ActivityKind.MEMORY2);
            CuptiInterop.cuptiActivityDisable(CuptiInterop.CUpti_ActivityKind.MARKER);
        }
        catch { /* CUPTI may already be torn down */ }
        _out.Writer.TryComplete();
        _started = false;
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<TransactionEvent> ReadEventsAsync(CancellationToken ct)
        => _out.Reader.ReadAllAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _instance = null;
    }

    // ------- CUPTI buffer callbacks (must be static-safe; use singleton) -------

    private static void OnBufferRequested(
        out IntPtr buffer, out UIntPtr size, out UIntPtr maxNumRecords)
    {
        const int bufSize = 8 * 1024 * 1024;   // 8 MiB
        buffer = Marshal.AllocHGlobal(bufSize);
        size   = (UIntPtr)bufSize;
        maxNumRecords = UIntPtr.Zero;
    }

    private static void OnBufferCompleted(
        IntPtr context, uint streamId, IntPtr buffer,
        UIntPtr size, UIntPtr validSize)
    {
        var self = _instance;
        if (self is not null)
        {
            try { self.ProcessBuffer(buffer, (long)validSize); }
            catch { /* Never let a parse error escape into CUPTI. */ }
        }
        Marshal.FreeHGlobal(buffer);
    }

    private void ProcessBuffer(IntPtr buffer, long validSize)
    {
        IntPtr record = IntPtr.Zero;
        while (true)
        {
            var rc = CuptiInterop.cuptiActivityGetNextRecord(buffer, (UIntPtr)validSize, out record);
            if (rc == CuptiInterop.CUptiResult.SUCCESS && record != IntPtr.Zero)
            {
                DispatchRecord(record);
            }
            else break;
        }
    }

    private void DispatchRecord(IntPtr record)
    {
        // First 4 bytes = kind.
        var kind = (CuptiInterop.CUpti_ActivityKind)(uint)Marshal.ReadInt32(record);
        switch (kind)
        {
            case CuptiInterop.CUpti_ActivityKind.CONCURRENT_KERNEL:
                DispatchKernel(Marshal.PtrToStructure<CuptiInterop.CUpti_ActivityKernel8>(record));
                break;
            case CuptiInterop.CUpti_ActivityKind.MEMCPY:
                DispatchMemcpy(Marshal.PtrToStructure<CuptiInterop.CUpti_ActivityMemcpy5>(record));
                break;
            case CuptiInterop.CUpti_ActivityKind.MEMORY2:
                DispatchMemory(Marshal.PtrToStructure<CuptiInterop.CUpti_ActivityMemory3>(record));
                break;
        }
    }

    private void DispatchKernel(CuptiInterop.CUpti_ActivityKernel8 k)
    {
        string name = k.namePtr == IntPtr.Zero
            ? "<unknown_kernel>"
            : Marshal.PtrToStringAnsi(k.namePtr) ?? "<unknown_kernel>";

        // Payload: name len (u16) | name | grids (3×u32) | blocks (3×u32) | dur_ns (u64)
        byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        byte[] payload = new byte[2 + nameBytes.Length + 3 * 4 + 3 * 4 + 8];
        int o = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(o), (ushort)nameBytes.Length); o += 2;
        Buffer.BlockCopy(nameBytes, 0, payload, o, nameBytes.Length); o += nameBytes.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(o), k.gridX); o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(o), k.gridY); o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(o), k.gridZ); o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(o), k.blockX); o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(o), k.blockY); o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(o), k.blockZ); o += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(o), k.end - k.start);

        var markers = new[] { new TraceMarker(
            Name: "cuda.kernel",
            BeginNs: (long)k.start, EndNs: (long)k.end,
            ColorRgba: 0xFF6E7A9AU, ThreadId: 0, StreamId: k.streamId,
            CorrelationId: k.correlationId) };

        Publish(new TransactionEvent(
            EventId: NextId(),
            TransactionId: _transactionId,
            TimestampNs: (long)k.start,
            Kind: Core.Transactions.EventKind.KernelLaunch,
            TokenIndex: -1, LayerIndex: -1, HeadIndex: -1,
            ThreadId: 0, StreamId: k.streamId, DeviceId: k.deviceId,
            Payload: payload, Markers: markers));
    }

    private void DispatchMemcpy(CuptiInterop.CUpti_ActivityMemcpy5 m)
    {
        byte[] payload = new byte[8 + 1 + 1 + 1 + 1];
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(0), m.bytes);
        payload[8]  = m.copyKind;
        payload[9]  = m.srcKind;
        payload[10] = m.dstKind;
        payload[11] = m.flags;

        var markers = new[] { new TraceMarker(
            Name: "cuda.memcpy",
            BeginNs: (long)m.start, EndNs: (long)m.end,
            ColorRgba: 0xFFB48F5BU, ThreadId: 0, StreamId: m.streamId,
            CorrelationId: m.correlationId) };

        Publish(new TransactionEvent(
            EventId: NextId(),
            TransactionId: _transactionId,
            TimestampNs: (long)m.start,
            Kind: Core.Transactions.EventKind.Memcpy,
            TokenIndex: -1, LayerIndex: -1, HeadIndex: -1,
            ThreadId: 0, StreamId: m.streamId, DeviceId: m.deviceId,
            Payload: payload, Markers: markers));
    }

    private void DispatchMemory(CuptiInterop.CUpti_ActivityMemory3 m)
    {
        byte[] payload = new byte[8 + 8];
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(0), m.address);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(8), m.bytes);

        Publish(new TransactionEvent(
            EventId: NextId(),
            TransactionId: _transactionId,
            TimestampNs: (long)m.timestamp,
            Kind: Core.Transactions.EventKind.Alloc,
            TokenIndex: -1, LayerIndex: -1, HeadIndex: -1,
            ThreadId: 0, StreamId: m.streamId, DeviceId: m.deviceId,
            Payload: payload,
            Markers: Array.Empty<TraceMarker>()));
    }

    private ulong NextId() => Interlocked.Increment(ref _nextEventId);

    private void Publish(TransactionEvent e)
    {
        // Non-blocking write with fallback: if the channel is full we spin
        // briefly in a Task to avoid stalling the CUPTI thread.
        if (!_out.Writer.TryWrite(e))
        {
            _ = Task.Run(async () =>
            {
                try { await _out.Writer.WriteAsync(e); } catch { }
            });
        }
    }

    private static void Check(CuptiInterop.CUptiResult r)
    {
        if (r != CuptiInterop.CUptiResult.SUCCESS)
            throw new InvalidOperationException($"CUPTI call failed: {r}");
    }
}
