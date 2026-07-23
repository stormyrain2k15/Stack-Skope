using System.Text;
using StackScope.Core.Transactions;

namespace StackScope.Core.Storage;

/// <summary>
/// The event store for a single inference transaction. Owns the mmap log
/// and the SQLite index. Appends are thread-safe. Reads are safe for any
/// number of concurrent readers (each read is guarded on the SQLite side
/// by the connection's own locking).
/// </summary>
public sealed class EventStore : IDisposable
{
    private readonly MmapEventLog _log;
    private readonly SqliteIndex _index;
    private readonly object _writeLock = new();
    private readonly Dictionary<string, uint> _markerIdCache = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>ULID of the transaction this store belongs to.</summary>
    public string TransactionId { get; }

    public EventStore(string transactionId, string storageDir)
    {
        TransactionId = transactionId;
        Directory.CreateDirectory(storageDir);
        _log   = new MmapEventLog(Path.Combine(storageDir, transactionId + ".mmap"));
        _index = new SqliteIndex(Path.Combine(storageDir, transactionId + ".sqlite"));
        _index.SetMeta("transaction_id", transactionId);
        _index.SetMeta("schema_version", "1");
    }

    public SqliteIndex Index => _index;

    public void Append(TransactionEvent e)
    {
        // Serialise the record into a rented buffer, then commit both the
        // log append and the index insert under a single lock so readers
        // never see an event without an index entry.
        var txidUtf8 = Encoding.UTF8.GetBytes(e.TransactionId);
        var size = EventRecord.SizeOf(txidUtf8.Length, e.Payload.Length, e.Markers.Count);
        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(size);
        try
        {
            lock (_writeLock)
            {
                EventRecord.Write(buf.AsSpan(0, size), e, txidUtf8, InternMarkerName);
                long offset = _log.Append(buf.AsSpan(0, size));
                _index.InsertEvent(
                    e.EventId, e.TimestampNs, (byte)e.Kind,
                    e.TokenIndex, e.LayerIndex, e.HeadIndex,
                    e.StreamId, e.ThreadId, e.DeviceId,
                    offset, size);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>
    /// Bulk append. Uses a single SQLite transaction for the whole batch.
    /// </summary>
    public void AppendMany(IReadOnlyList<TransactionEvent> events)
    {
        lock (_writeLock)
        {
            using var tx = _index.BeginBatch();
            foreach (var e in events)
            {
                var txidUtf8 = Encoding.UTF8.GetBytes(e.TransactionId);
                var size = EventRecord.SizeOf(txidUtf8.Length, e.Payload.Length, e.Markers.Count);
                var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(size);
                try
                {
                    EventRecord.Write(buf.AsSpan(0, size), e, txidUtf8, InternMarkerName);
                    long offset = _log.Append(buf.AsSpan(0, size));
                    _index.InsertEvent(
                        e.EventId, e.TimestampNs, (byte)e.Kind,
                        e.TokenIndex, e.LayerIndex, e.HeadIndex,
                        e.StreamId, e.ThreadId, e.DeviceId,
                        offset, size);
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(buf);
                }
            }
            tx.Commit();
        }
    }

    /// <summary>Read a single event by its file offset (obtained from the index).</summary>
    public TransactionEvent ReadAt(long offset)
    {
        byte[] rec = _log.ReadRecord(offset);
        return EventRecord.Read(rec, LookupMarkerName);
    }

    public void Flush() => _log.Flush();

    private uint InternMarkerName(string name)
    {
        if (_markerIdCache.TryGetValue(name, out var id)) return id;
        id = _index.InternMarkerName(name);
        _markerIdCache[name] = id;
        return id;
    }

    private string LookupMarkerName(uint id) => _index.LookupMarkerName(id);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _log.Flush();
        _log.Dispose();
        _index.Dispose();
    }
}
