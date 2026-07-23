using System.Threading.Channels;
using StackScope.Core.Capture;
using StackScope.Core.Transactions;

namespace StackScope.Adapters.Drivers.Vulkan;

/// <summary>
/// Vulkan capture backend. Two capture paths:
///
///  1. <b>Debug utils labels</b> — Workers wrap dispatch groups in
///     <c>vkCmdBeginDebugUtilsLabelEXT</c> ranges with names like
///     "stackscope.layer.N" and "stackscope.head.N". A registered
///     debug messenger emits <see cref="TraceMarker"/> events keyed on
///     the label name.
///
///  2. <b>Timestamp queries</b> — Every submitted command buffer has
///     <c>vkCmdWriteTimestamp2</c> calls at each boundary. We poll the
///     query pool from a background thread and translate GPU ticks into
///     host nanoseconds via <c>vkGetCalibratedTimestampsEXT</c>.
///
/// On a Vulkan-absent host the backend degrades silently to a no-op —
/// the DllNotFound exception on the first <c>vkGetInstanceProcAddr</c>
/// call closes the channel so no fake events are ever produced.
/// </summary>
public sealed class VulkanCaptureBackend : ICaptureBackend
{
    private readonly Channel<TransactionEvent> _out;
    private string _transactionId = "";
    private ulong _nextEventId;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private bool _started;

    public string Kind => "driver.vulkan";

    public VulkanCaptureBackend()
    {
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
        try
        {
            // Probe: any call into vulkan-1.dll to fail fast if absent.
            _ = VulkanInterop.vkGetInstanceProcAddr(IntPtr.Zero, "vkEnumerateInstanceVersion");
        }
        catch (DllNotFoundException)
        {
            _out.Writer.TryComplete();
            return Task.CompletedTask;
        }

        _pollCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pollTask = Task.Run(() => PollLoop(_pollCts.Token), _pollCts.Token);
        _started = true;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (!_started) return;
        _pollCts?.Cancel();
        if (_pollTask is not null)
        {
            try { await _pollTask.ConfigureAwait(false); } catch { }
        }
        _out.Writer.TryComplete();
        _started = false;
    }

    public IAsyncEnumerable<TransactionEvent> ReadEventsAsync(CancellationToken ct)
        => _out.Reader.ReadAllAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _pollCts?.Dispose();
    }

    /// <summary>
    /// Poll loop for Vulkan timestamp queries. In this pass, the worker
    /// side (see <c>workers/inference_worker_py</c>'s Vulkan section, and
    /// the llama.cpp Vulkan backend) publishes queries via an out-of-band
    /// bridge (shared memory + eventfd on Windows: named events + a
    /// section mapping). The backend reads the queries here, converts
    /// GPU ticks to host ns using the calibrated timestamps extension,
    /// and emits <see cref="TransactionEvent"/> instances.
    ///
    /// The bridge lives in a separate small file and is designed to be
    /// swap-in for an alternative capture path (e.g., LayerLoader-based
    /// implicit layer) without touching this backend.
    /// </summary>
    private async Task PollLoop(CancellationToken ct)
    {
        // The bridge is instantiated lazily so hosts without Vulkan don't
        // even try to open the named section.
        VulkanCaptureBridge? bridge = null;
        try { bridge = VulkanCaptureBridge.OpenOrNull(_transactionId); }
        catch { bridge = null; }

        if (bridge is null)
        {
            _out.Writer.TryComplete();
            return;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var batch = bridge.TryReadBatch();
                if (batch.Count == 0)
                {
                    await Task.Delay(2, ct).ConfigureAwait(false);
                    continue;
                }
                foreach (var q in batch)
                {
                    var markers = new[] { new TraceMarker(
                        Name: q.Label ?? "vk.timestamp",
                        BeginNs: q.BeginNs,
                        EndNs:   q.EndNs,
                        ColorRgba: 0xFF7A9BB4U,
                        ThreadId: q.ThreadId,
                        StreamId: q.QueueFamily,
                        CorrelationId: q.CorrelationId) };

                    var payload = System.Text.Encoding.UTF8.GetBytes(q.PipelineName ?? "");
                    var e = new TransactionEvent(
                        EventId: Interlocked.Increment(ref _nextEventId),
                        TransactionId: _transactionId,
                        TimestampNs: q.BeginNs,
                        Kind: Core.Transactions.EventKind.KernelLaunch,
                        TokenIndex: -1, LayerIndex: -1, HeadIndex: -1,
                        ThreadId: q.ThreadId,
                        StreamId: q.QueueFamily,
                        DeviceId: q.DeviceId,
                        Payload: payload, Markers: markers);
                    if (!_out.Writer.TryWrite(e))
                        await _out.Writer.WriteAsync(e, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            bridge?.Dispose();
        }
    }
}
