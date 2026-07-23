using StackScope.Adapters.Runtimes;
using StackScope.Core.Capture;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;

namespace StackScope.Services;

/// <summary>
/// Orchestrates a single inference transaction end-to-end:
///  1. Assigns a ULID.
///  2. Opens an <see cref="EventStore"/> under the project's captures dir.
///  3. Wires the runtime adapter's event stream into the capture pipeline
///     alongside all registered driver backends.
///  4. Records started/ended/completed metadata into the store.
///
/// Cancellation, worker crash, and partial-capture-on-crash are all
/// handled explicitly — the store is fully flushed and the "completed"
/// meta row is set even in the failure paths, so a partial capture is
/// still openable.
/// </summary>
public sealed class CaptureService
{
    private readonly ProjectService _project;

    public CaptureService(ProjectService project) { _project = project; }

    public async Task<InferenceTransaction> RunAsync(
        IRuntimeAdapter runtime,
        RunInferenceArgs argsWithoutTxid,
        IReadOnlyList<ICaptureBackend> driverBackends,
        CancellationToken ct)
    {
        var txid = Ulid.NewUlid();
        var args = argsWithoutTxid with { TransactionId = txid };

        var store = _project.OpenOrCreateStore(txid);
        var txn = new InferenceTransaction
        {
            TransactionId = txid,
            ModelHandle = args.ModelHandle,
            Architecture = "",
            StartedNs = NowNs(),
        };
        store.Index.SetMeta("transaction_id", txid);
        store.Index.SetMeta("model_handle", args.ModelHandle);
        store.Index.SetMeta("started_ns", txn.StartedNs.ToString());
        store.Index.SetMeta("completed", "false");
        // Persist the run params so ProjectService.ListTransactions can
        // expose them (prompt for auto-compare match, ablate_layer/head
        // for the WasAblated predicate). Empty prompt is stored as empty
        // string so the reader always finds the key.
        store.Index.SetMeta("prompt", args.Prompt ?? "");
        store.Index.SetMeta("ablate_layer",     args.AblateLayer.ToString());
        store.Index.SetMeta("ablate_head",      args.AblateHead.ToString());
        store.Index.SetMeta("ablate_layer_end", args.AblateLayerEnd.ToString());
        store.Index.SetMeta("ablate_head_end",  args.AblateHeadEnd.ToString());

        var pipeline = new CapturePipeline(store);
        foreach (var b in driverBackends) pipeline.Register(b);

        await pipeline.StartAsync(ct).ConfigureAwait(false);

        // Forward the worker's event stream into the store directly. We
        // do NOT wrap the worker in an ICaptureBackend to avoid an extra
        // channel hop for the highest-volume producer.
        try
        {
            await foreach (var e in runtime.RunInferenceAsync(args, ct).ConfigureAwait(false))
            {
                store.Append(e);
                txn.EventCount++;
                if (e.Kind == EventKind.TokenEnd) txn.TokenCount++;
            }
            txn.Completed = true;
        }
        catch (OperationCanceledException)
        {
            txn.Error = "cancelled";
        }
        catch (Exception ex)
        {
            txn.Error = ex.Message;
        }
        finally
        {
            try { await pipeline.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            await pipeline.DisposeAsync().ConfigureAwait(false);
            txn.EndedNs = NowNs();
            store.Index.SetMeta("ended_ns", txn.EndedNs.ToString());
            store.Index.SetMeta("completed", txn.Completed ? "true" : "false");
            if (txn.Error is not null) store.Index.SetMeta("error", txn.Error);
            store.Dispose();
        }
        return txn;
    }

    private static long NowNs()
        => (long)((double)System.Diagnostics.Stopwatch.GetTimestamp() /
                  System.Diagnostics.Stopwatch.Frequency * 1e9);
}
