using StackScope.Core.Transactions;

namespace StackScope.Core.Capture;

/// <summary>
/// A capture backend produces events from some source (a Python worker,
/// a llama.cpp worker, CUPTI, rocprofiler, ETW, CPU counters). The
/// pipeline pulls events from all registered backends and writes them
/// into the EventStore.
/// </summary>
public interface ICaptureBackend : IAsyncDisposable
{
    string Kind { get; }                    // "worker.pytorch", "driver.cuda", ...
    Task StartAsync(string transactionId, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    IAsyncEnumerable<TransactionEvent> ReadEventsAsync(CancellationToken ct);
}
