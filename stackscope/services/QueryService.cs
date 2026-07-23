using StackScope.Core.Queries;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;

namespace StackScope.Services;

/// <summary>
/// UI-facing query service. Opens the SQLite index for a transaction
/// on demand and closes it eagerly to keep locks short. For hot
/// transactions (currently open by <see cref="CaptureService"/>) the
/// caller must supply the live <see cref="EventStore"/> — we never open
/// a second connection to a WAL SQLite while it's being written to.
/// </summary>
public sealed class QueryService
{
    private readonly ProjectService _project;

    public QueryService(ProjectService project) { _project = project; }

    public IReadOnlyList<TransactionEvent> Query(string transactionId, EventQuery q,
                                                 EventStore? liveStore = null)
    {
        if (liveStore is not null && liveStore.TransactionId == transactionId)
            return new QueryEngine(liveStore).Query(q).ToList();

        using var store = new EventStore(transactionId, _project.CapturesDir);
        return new QueryEngine(store).Query(q).ToList();
    }

    public long Count(string transactionId, EventQuery q, EventStore? liveStore = null)
    {
        if (liveStore is not null && liveStore.TransactionId == transactionId)
            return new QueryEngine(liveStore).Count(q);

        using var store = new EventStore(transactionId, _project.CapturesDir);
        return new QueryEngine(store).Count(q);
    }
}
