using System.Buffers.Binary;
using StackScope.Core.Queries;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;

namespace StackScope.Core.Analysis;

/// <summary>
/// Attribution graph. Extends <see cref="CircuitTracer"/> from a linear
/// backwards walk to a weighted causal graph rooted at the output
/// token. Nodes are events; edges carry a weight approximating
/// contribution.
///
/// Weights are a coarse first-pass:
///   * attention edges use max_prob from the AttentionScores payload
///   * activation edges use their L2 energy (from AbsEnergy summary)
///   * logits edges use the top-1 probability
///
/// Good enough to rank "which heads mattered for this token" in the
/// UI. A rigorous integrated-gradients version can plug into the same
/// node/edge shape later.
/// </summary>
public sealed class AttributionGraph
{
    public sealed record Node(ulong EventId, int Layer, int Head, EventKind Kind, string Label);
    public sealed record Edge(ulong From, ulong To, double Weight, string Reason);
    public sealed record Result(int TargetToken, IReadOnlyList<Node> Nodes, IReadOnlyList<Edge> Edges);

    private readonly EventStore _store;
    public AttributionGraph(EventStore store) { _store = store; }

    public Result Build(int targetToken, int maxNodesPerLayer = 8, int maxDepthLayers = 32)
    {
        var qe = new QueryEngine(_store);
        var nodes = new List<Node>();
        var edges = new List<Edge>();

        // Root: the LOGITS event for the target token.
        TransactionEvent? logits = null;
        foreach (var e in qe.Query(new EventQuery
        {
            Kinds = new[] { EventKind.Logits },
            TokenIndex = new IntRange(targetToken, targetToken),
            Limit = 1,
        }))
        {
            logits = e; break;
        }
        if (logits is null)
            return new Result(targetToken, nodes, edges);

        nodes.Add(new Node(logits.EventId, -1, -1, EventKind.Logits, $"logits@t{targetToken}"));

        // Walk backwards layer by layer, taking the top-N attention
        // events by max_prob.
        for (int layer = maxDepthLayers - 1; layer >= 0; layer--)
        {
            var attn = new List<(TransactionEvent e, double w)>();
            foreach (var e in qe.Query(new EventQuery
            {
                Kinds = new[] { EventKind.AttentionScores },
                TokenIndex = new IntRange(targetToken, targetToken),
                LayerIndex = new IntRange(layer, layer),
                Limit = 512,
            }))
            {
                double w = ExtractMaxProb(e);
                attn.Add((e, w));
            }
            if (attn.Count == 0) continue;
            attn.Sort((x, y) => y.w.CompareTo(x.w));
            var top = attn.Take(maxNodesPerLayer).ToList();

            ulong parent = layer == maxDepthLayers - 1 ? logits.EventId
                : (nodes.Where(n => n.Layer == layer + 1).Select(n => n.EventId).FirstOrDefault());
            if (parent == 0) parent = logits.EventId;

            foreach (var (e, w) in top)
            {
                nodes.Add(new Node(e.EventId, layer, e.HeadIndex,
                    EventKind.AttentionScores,
                    $"L{layer:D2}·H{e.HeadIndex:D2}·max_prob={w:F3}"));
                edges.Add(new Edge(parent, e.EventId, w, "attention.max_prob"));
            }
        }

        return new Result(targetToken, nodes, edges);
    }

    private static double ExtractMaxProb(TransactionEvent e)
    {
        // Payload from pack_head_stats (see attention_capture.py):
        //   (head:i32, mean:f32, std:f32, entropy:f32, max_prob:f32, argmax:i32).
        if (e.Payload.Length < 24) return 0.0;
        var p = e.Payload.Span;
        return BinaryPrimitives.ReadSingleLittleEndian(p.Slice(16, 4));
    }
}
