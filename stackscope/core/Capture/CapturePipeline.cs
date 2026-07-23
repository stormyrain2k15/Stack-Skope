using System.Threading.Channels;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;

namespace StackScope.Core.Capture;

/// <summary>
/// Multiplexes any number of <see cref="ICaptureBackend"/>s into the
/// single append-only <see cref="EventStore"/> for a transaction.
/// Backpressure is bounded — if a backend outpaces disk, that backend
/// waits, but other backends aren't blocked.
/// </summary>
public sealed class CapturePipeline : IAsyncDisposable
{
    private readonly EventStore _store;
    private readonly List<ICaptureBackend> _backends = new();
    private readonly List<Task> _pumps = new();
    private readonly Channel<TransactionEvent> _bus =
        Channel.CreateBounded<TransactionEvent>(
            new BoundedChannelOptions(capacity: 8192)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait
            });
    private CancellationTokenSource? _cts;
    private Task? _writerTask;

    public CapturePipeline(EventStore store) { _store = store; }

    public void Register(ICaptureBackend backend) => _backends.Add(backend);

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        foreach (var b in _backends)
            await b.StartAsync(_store.TransactionId, _cts.Token).ConfigureAwait(false);

        foreach (var b in _backends)
        {
            _pumps.Add(Task.Run(() => Pump(b, _cts.Token), _cts.Token));
        }

        _writerTask = Task.Run(() => WriterLoop(_cts.Token), _cts.Token);
    }

    private async Task Pump(ICaptureBackend backend, CancellationToken ct)
    {
        await foreach (var evt in backend.ReadEventsAsync(ct).ConfigureAwait(false))
        {
            await _bus.Writer.WriteAsync(evt, ct).ConfigureAwait(false);
        }
    }

    private async Task WriterLoop(CancellationToken ct)
    {
        // Drain the channel in batches of up to 256 to amortise SQLite tx cost.
        var batch = new List<TransactionEvent>(256);
        try
        {
            while (await _bus.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < 256 && _bus.Reader.TryRead(out var e))
                    batch.Add(e);
                if (batch.Count > 0) _store.AppendMany(batch);
            }
        }
        catch (OperationCanceledException) { /* graceful */ }
        finally
        {
            _store.Flush();
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        // Ask backends to stop producing.
        foreach (var b in _backends) await b.StopAsync(ct).ConfigureAwait(false);
        // Let pumps finish naturally, then complete the bus.
        try { await Task.WhenAll(_pumps).ConfigureAwait(false); } catch { }
        _bus.Writer.TryComplete();
        if (_writerTask is not null) await _writerTask.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_cts is not null && !_cts.IsCancellationRequested) _cts.Cancel();
            _bus.Writer.TryComplete();
            if (_writerTask is not null)
            {
                try { await _writerTask.ConfigureAwait(false); } catch { }
            }
            foreach (var b in _backends)
                await b.DisposeAsync().ConfigureAwait(false);
            _cts?.Dispose();
        }
        catch { }
    }
}
