using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading.Channels;
using StackScope.Core.Capture;
using StackScope.Core.Transactions;

namespace StackScope.Adapters.Drivers.Cpu;

/// <summary>
/// CPU capture backend. Samples the host process's own performance
/// counters at a fixed rate and emits them as <see cref="TransactionEvent"/>
/// records with <see cref="Core.Transactions.EventKind.Marker"/> kind.
///
/// The point isn't a full CPU profiler — that's what Windows Performance
/// Analyzer / ETW / perf are for. It's to give the UI a lightweight
/// "what is the worker's host thread doing right now" signal so users
/// can spot host-side stalls without adding an ETW dependency.
/// </summary>
public sealed class CpuCaptureBackend : ICaptureBackend
{
    private readonly Channel<TransactionEvent> _out;
    private readonly int _sampleHz;
    private string _transactionId = "";
    private ulong _nextEventId;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public string Kind => "driver.cpu";

    public CpuCaptureBackend(int sampleHz = 100)
    {
        if (sampleHz <= 0 || sampleHz > 10_000)
            throw new ArgumentOutOfRangeException(nameof(sampleHz));
        _sampleHz = sampleHz;
        _out = Channel.CreateBounded<TransactionEvent>(
            new BoundedChannelOptions(capacity: 2048)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });
    }

    public Task StartAsync(string transactionId, CancellationToken ct)
    {
        _transactionId = transactionId;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loopTask = Task.Run(() => Sample(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask.ConfigureAwait(false); } catch { }
        }
        _out.Writer.TryComplete();
    }

    public IAsyncEnumerable<TransactionEvent> ReadEventsAsync(CancellationToken ct)
        => _out.Reader.ReadAllAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _cts?.Dispose();
    }

    private async Task Sample(CancellationToken ct)
    {
        var proc = Process.GetCurrentProcess();
        long tickPeriodMs = Math.Max(1, 1000 / _sampleHz);
        TimeSpan prevCpu = proc.TotalProcessorTime;
        long prevWall = Stopwatch.GetTimestamp();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(tickPeriodMs), ct)
                          .ConfigureAwait(false);
                proc.Refresh();
                TimeSpan cpu = proc.TotalProcessorTime;
                long wall = Stopwatch.GetTimestamp();
                double cpuMs  = (cpu - prevCpu).TotalMilliseconds;
                double wallMs = (double)(wall - prevWall) / Stopwatch.Frequency * 1000.0;
                float cpuPct = wallMs > 0 ? (float)(cpuMs / wallMs) : 0f;

                long rss = proc.WorkingSet64;
                long pageFaults = 0; // Not portable via Process; leave as 0.

                prevCpu = cpu; prevWall = wall;

                byte[] payload = new byte[4 + 8 + 8];
                BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(0), cpuPct);
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(4), rss);
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(12), pageFaults);

                var evt = new TransactionEvent(
                    EventId: Interlocked.Increment(ref _nextEventId),
                    TransactionId: _transactionId,
                    TimestampNs: NowNs(),
                    Kind: Core.Transactions.EventKind.Marker,
                    TokenIndex: -1, LayerIndex: -1, HeadIndex: -1,
                    ThreadId: Environment.CurrentManagedThreadId,
                    StreamId: -1, DeviceId: -1,
                    Payload: payload,
                    Markers: new[] { new TraceMarker(
                        "cpu.sample", NowNs(), 0, 0xFF6B6B6BU,
                        Environment.CurrentManagedThreadId, -1, 0) });

                _out.Writer.TryWrite(evt);
            }
        }
        catch (OperationCanceledException) { /* graceful */ }
    }

    private static long NowNs()
        => (long)((double)Stopwatch.GetTimestamp() / Stopwatch.Frequency * 1e9);
}
