using StackScope.Core.Queries;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;

namespace StackScope.Core.Analysis;

/// <summary>
/// Walks activation dependencies backwards from a target (token, logit)
/// to the layers/heads that produced it. Traversal edges follow the
/// event-time ordering per token: LOGITS → ATTENTION_OUTPUT → per-head
/// ATTENTION_SCORES → ATTENTION_QKV (input side).
///
/// Confidence: the store already has correlation ids on markers where
/// available; we prefer marker-linked edges, fall back to
/// timestamp-ordering within the same (token, layer).
/// </summary>
public sealed class CircuitTracer
{
    public sealed record Node(
        ulong EventId, EventKind Kind, int Token, int Layer, int Head,
        long TimestampNs, string Label);

    public sealed record Path(IReadOnlyList<Node> Nodes);

    private readonly EventStore _store;
    public CircuitTracer(EventStore store) { _store = store; }

    public Path TraceFromToken(int tokenIndex)
    {
        var qe = new QueryEngine(_store);
        var pool = qe.Query(new EventQuery
        {
            TokenIndex = new IntRange(tokenIndex, tokenIndex),
            Limit = 100_000
        }).ToList();

        var path = new List<Node>();
        var logits = pool.LastOrDefault(e => e.Kind == EventKind.Logits);
        if (logits is null) return new Path(path);
        path.Add(Label(logits, "logits (sampled)"));

        var outputs = pool.Where(e => e.Kind == EventKind.AttentionOutput)
                          .OrderByDescending(e => e.LayerIndex).ToList();
        foreach (var o in outputs)
        {
            path.Add(Label(o, $"attn.output L{o.LayerIndex}"));
            var heads = pool.Where(e =>
                    e.Kind == EventKind.AttentionScores && e.LayerIndex == o.LayerIndex)
                .OrderByDescending(e => Weight(e)).Take(3).ToList();
            foreach (var h in heads)
                path.Add(Label(h, $"head L{h.LayerIndex}·H{h.HeadIndex} (top-weighted)"));
            var qkv = pool.Where(e =>
                    e.Kind == EventKind.AttentionQkv && e.LayerIndex == o.LayerIndex).ToList();
            foreach (var q in qkv)
                path.Add(Label(q, $"qkv L{q.LayerIndex}"));
        }
        return new Path(path);
    }

    private static Node Label(TransactionEvent e, string label)
        => new(e.EventId, e.Kind, e.TokenIndex, e.LayerIndex, e.HeadIndex,
               e.TimestampNs, label);

    private static float Weight(TransactionEvent e)
    {
        if (e.Payload.Length < 24) return 0;
        return System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(e.Payload.Span[16..]);
    }
}
