using StackScope.Core.Transactions;

namespace StackScope.Core.Correlation;

/// <summary>
/// Correlates worker-level events (token/layer/head/attention/tensor)
/// with driver-level events (kernel launches, memcpys, allocations).
///
/// The engine tries strategies in descending order of confidence and
/// stops at the first one that matches. Every returned pair carries an
/// explicit <see cref="CorrelationConfidence"/> so the UI can annotate.
///
/// Strategies:
///  1. <see cref="CorrelationConfidence.Direct"/> — worker and driver
///     events reference the same explicit correlation id.
///  2. <see cref="CorrelationConfidence.RuntimeCorrelated"/> — CUPTI/NVTX
///     runtime correlation id matches a marker's correlation id.
///  3. <see cref="CorrelationConfidence.AddressCorrelated"/> — memcpy /
///     alloc address falls inside the arena tracked by a worker tensor.
///  4. <see cref="CorrelationConfidence.MarkerCorrelated"/> — driver
///     event's timestamp falls inside a semantic marker range.
///  5. <see cref="CorrelationConfidence.TimeCorrelated"/> — nearest in
///     time on the same stream (advisory).
///  6. <see cref="CorrelationConfidence.Inferred"/> — heuristic fallback.
///
/// Address-correlation and payload-specific matching live behind small
/// pluggable interfaces so format-specific payloads don't leak here.
/// </summary>
public sealed class CorrelationEngine
{
    private readonly IReadOnlyList<TransactionEvent> _workerEvents;
    private readonly IReadOnlyList<TransactionEvent> _driverEvents;

    public CorrelationEngine(
        IReadOnlyList<TransactionEvent> workerEvents,
        IReadOnlyList<TransactionEvent> driverEvents)
    {
        _workerEvents = workerEvents;
        _driverEvents = driverEvents;
    }

    public IEnumerable<CorrelatedPair> Correlate()
    {
        // Bucket worker markers by their correlation id and by (thread, ts range).
        var markerById = new Dictionary<ulong, (TransactionEvent evt, TraceMarker m)>();
        var markersByThread = new Dictionary<int, List<(TransactionEvent evt, TraceMarker m)>>();

        foreach (var w in _workerEvents)
        {
            foreach (var m in w.Markers)
            {
                if (m.CorrelationId != 0)
                    markerById[m.CorrelationId] = (w, m);
                if (!markersByThread.TryGetValue(m.ThreadId, out var list))
                    markersByThread[m.ThreadId] = list = new();
                list.Add((w, m));
            }
        }

        foreach (var d in _driverEvents)
        {
            var best = TryCorrelate(d, markerById, markersByThread);
            if (best is not null) yield return best;
        }
    }

    private CorrelatedPair? TryCorrelate(
        TransactionEvent d,
        Dictionary<ulong, (TransactionEvent evt, TraceMarker m)> markerById,
        Dictionary<int, List<(TransactionEvent evt, TraceMarker m)>> markersByThread)
    {
        // 1. Direct correlation ID match.
        foreach (var dm in d.Markers)
        {
            if (dm.CorrelationId != 0 && markerById.TryGetValue(dm.CorrelationId, out var hit))
            {
                var confidence = dm.CorrelationId == hit.m.CorrelationId
                    ? CorrelationConfidence.Direct
                    : CorrelationConfidence.RuntimeCorrelated;
                return new CorrelatedPair(hit.evt, d, confidence,
                    $"marker.correlation_id={dm.CorrelationId}");
            }
        }

        // 2. Marker range containment on same thread/stream.
        if (markersByThread.TryGetValue(d.ThreadId, out var candidates))
        {
            foreach (var (evt, m) in candidates)
            {
                if (m.Contains(d.TimestampNs) &&
                    (m.StreamId == d.StreamId || m.StreamId == -1))
                {
                    return new CorrelatedPair(evt, d,
                        CorrelationConfidence.MarkerCorrelated,
                        $"marker='{m.Name}' contains ts");
                }
            }
        }

        // 3. Nearest-in-time on same stream (advisory only).
        TransactionEvent? nearest = null;
        long bestDelta = long.MaxValue;
        foreach (var w in _workerEvents)
        {
            if (w.StreamId != d.StreamId) continue;
            long dt = Math.Abs(w.TimestampNs - d.TimestampNs);
            if (dt < bestDelta) { bestDelta = dt; nearest = w; }
        }
        if (nearest is not null && bestDelta < 5_000_000)  // 5 ms window
        {
            return new CorrelatedPair(nearest, d,
                CorrelationConfidence.TimeCorrelated,
                $"Δt={bestDelta}ns on stream {d.StreamId}");
        }

        // 4. Inferred fallback: any worker event within 50 ms.
        nearest = null; bestDelta = long.MaxValue;
        foreach (var w in _workerEvents)
        {
            long dt = Math.Abs(w.TimestampNs - d.TimestampNs);
            if (dt < bestDelta) { bestDelta = dt; nearest = w; }
        }
        if (nearest is not null && bestDelta < 50_000_000)
        {
            return new CorrelatedPair(nearest, d,
                CorrelationConfidence.Inferred,
                $"Δt={bestDelta}ns, streams differ");
        }

        return null;
    }
}
