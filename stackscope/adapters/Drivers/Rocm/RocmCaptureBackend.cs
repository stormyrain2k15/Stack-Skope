using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using StackScope.Core.Capture;
using StackScope.Core.Transactions;

namespace StackScope.Adapters.Drivers.Rocm;

/// <summary>
/// ROCm capture backend using rocprofiler/roctracer for HIP kernel
/// dispatch and memcpy activity. Correlation IDs are surfaced identically
/// to CUPTI, so the correlation engine can match rocTX markers coming
/// from workers regardless of vendor.
/// </summary>
public sealed class RocmCaptureBackend : ICaptureBackend
{
    private readonly Channel<TransactionEvent> _out;
    private string _transactionId = "";
    private ulong _nextEventId = 0;
    private static RocmCaptureBackend? _instance;
    private RocProfilerInterop.ActivityCallback? _cbDelegate;
    private bool _started;
    private readonly int _deviceId;

    public string Kind => "driver.rocm";

    public RocmCaptureBackend(int deviceId = 0)
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
        _transactionId = transactionId;
        _instance = this;
        _cbDelegate = OnActivity;

        try
        {
            Check(RocProfilerInterop.rocprofiler_initialize(), "rocprofiler_initialize");
            Check(RocProfilerInterop.roctracer_open_pool(IntPtr.Zero), "roctracer_open_pool");
            Check(RocProfilerInterop.roctracer_start(), "roctracer_start");
            _started = true;
        }
        catch (DllNotFoundException)
        {
            // ROCm runtime not installed on this host — the backend
            // becomes a no-op. UI will not show ROCm data.
            _out.Writer.TryComplete();
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        if (!_started) return Task.CompletedTask;
        try
        {
            RocProfilerInterop.roctracer_flush_activity();
            RocProfilerInterop.roctracer_stop();
            RocProfilerInterop.rocprofiler_finalize();
        }
        catch { /* ignore during teardown */ }
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

    private static void OnActivity(IntPtr begin, IntPtr end, IntPtr userArg)
    {
        var self = _instance;
        if (self is null) return;
        try
        {
            long span = end.ToInt64() - begin.ToInt64();
            IntPtr p = begin;
            // Walk records; each begins with a 4-byte domain field. We
            // dispatch on (domain, op) which occupies the first 8 bytes.
            while (p.ToInt64() < end.ToInt64())
            {
                uint domain = (uint)Marshal.ReadInt32(p);
                uint op     = (uint)Marshal.ReadInt32(p, 4);
                if (op == 1) // HIP_OP_ID_DISPATCH
                {
                    var k = Marshal.PtrToStructure<RocProfilerInterop.KernelRecord>(p);
                    self.DispatchKernel(k);
                    p = IntPtr.Add(p, Marshal.SizeOf<RocProfilerInterop.KernelRecord>());
                }
                else if (op == 2) // HIP_OP_ID_COPY
                {
                    var c = Marshal.PtrToStructure<RocProfilerInterop.CopyRecord>(p);
                    self.DispatchCopy(c);
                    p = IntPtr.Add(p, Marshal.SizeOf<RocProfilerInterop.CopyRecord>());
                }
                else
                {
                    // Unknown op — safest to bail out of this buffer.
                    break;
                }
            }
        }
        catch { /* never propagate into rocprofiler */ }
    }

    private void DispatchKernel(RocProfilerInterop.KernelRecord k)
    {
        string name = k.kernelName == IntPtr.Zero
            ? "<unknown_kernel>"
            : Marshal.PtrToStringAnsi(k.kernelName) ?? "<unknown_kernel>";
        byte[] nb = System.Text.Encoding.UTF8.GetBytes(name);

        byte[] payload = new byte[2 + nb.Length + 3 * 4 + 3 * 4 + 8];
        int o = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(o), (ushort)nb.Length); o += 2;
        Buffer.BlockCopy(nb, 0, payload, o, nb.Length); o += nb.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(o), k.gridX); o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(o), k.gridY); o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(o), k.gridZ); o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(o), k.blockX); o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(o), k.blockY); o += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(o), k.blockZ); o += 4;
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(o), k.endNs - k.beginNs);

        var markers = new[] { new TraceMarker(
            Name: "hip.kernel",
            BeginNs: (long)k.beginNs, EndNs: (long)k.endNs,
            ColorRgba: 0xFFAA5545U, ThreadId: (int)k.threadId,
            StreamId: (int)(k.queueId & 0x7FFFFFFF),
            CorrelationId: k.correlationId) };

        Publish(new TransactionEvent(
            EventId: NextId(), TransactionId: _transactionId,
            TimestampNs: (long)k.beginNs,
            Kind: Core.Transactions.EventKind.KernelLaunch,
            TokenIndex: -1, LayerIndex: -1, HeadIndex: -1,
            ThreadId: (int)k.threadId,
            StreamId: (int)(k.queueId & 0x7FFFFFFF),
            DeviceId: (int)k.deviceId,
            Payload: payload, Markers: markers));
    }

    private void DispatchCopy(RocProfilerInterop.CopyRecord c)
    {
        byte[] payload = new byte[8 + 1 + 3];
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(0), c.bytes);
        payload[8] = c.copyKind;

        var markers = new[] { new TraceMarker(
            Name: "hip.memcpy",
            BeginNs: (long)c.beginNs, EndNs: (long)c.endNs,
            ColorRgba: 0xFFB48F5BU, ThreadId: (int)c.threadId,
            StreamId: (int)(c.queueId & 0x7FFFFFFF),
            CorrelationId: c.correlationId) };

        Publish(new TransactionEvent(
            EventId: NextId(), TransactionId: _transactionId,
            TimestampNs: (long)c.beginNs,
            Kind: Core.Transactions.EventKind.Memcpy,
            TokenIndex: -1, LayerIndex: -1, HeadIndex: -1,
            ThreadId: (int)c.threadId,
            StreamId: (int)(c.queueId & 0x7FFFFFFF),
            DeviceId: (int)c.deviceId,
            Payload: payload, Markers: markers));
    }

    private ulong NextId() => Interlocked.Increment(ref _nextEventId);

    private void Publish(TransactionEvent e)
    {
        if (!_out.Writer.TryWrite(e))
        {
            _ = Task.Run(async () =>
            {
                try { await _out.Writer.WriteAsync(e); } catch { }
            });
        }
    }

    private static void Check(RocProfilerInterop.RocStatus s, string what)
    {
        if (s != RocProfilerInterop.RocStatus.Success)
            throw new InvalidOperationException($"rocprofiler {what} failed: {s}");
    }
}
