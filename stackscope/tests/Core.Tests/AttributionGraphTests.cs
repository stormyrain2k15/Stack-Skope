using System.Buffers.Binary;
using StackScope.Core.Analysis;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;
using Xunit;

namespace StackScope.Core.Tests;

public class AttributionGraphTests
{
    [Fact]
    public void Builds_Graph_Rooted_At_Target_Token()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ss-ag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string tx = Ulid.NewUlid();
        try
        {
            using (var s = new EventStore(tx, dir))
            {
                // Logits for target token = 1
                s.Append(Logits(tx, 1, 1));
                // Attention scores for token=1 across 2 layers, several heads
                for (int layer = 0; layer < 2; layer++)
                for (int head = 0; head < 4; head++)
                    s.Append(AttentionScores(tx, (ulong)(10 + layer * 4 + head),
                        tok: 1, layer: layer, head: head,
                        maxProb: 0.1f + head * 0.2f));
            }

            using var s2 = new EventStore(tx, dir);
            var r = new AttributionGraph(s2).Build(targetToken: 1, maxNodesPerLayer: 2, maxDepthLayers: 4);
            Assert.Equal(1, r.TargetToken);
            Assert.NotEmpty(r.Nodes);
            // Top-N ranking means only the highest-max_prob heads are picked
            var heads = r.Nodes.Where(n => n.Kind == EventKind.AttentionScores).Select(n => n.Head).ToList();
            Assert.Contains(3, heads); // highest max_prob head must be in there
            Assert.NotEmpty(r.Edges);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Returns_Empty_When_No_Logits_For_Target()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ss-ag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string tx = Ulid.NewUlid();
        try
        {
            using (var s = new EventStore(tx, dir))
                s.Append(Logits(tx, 1, tok: 0)); // logits for a different token
            using var s2 = new EventStore(tx, dir);
            var r = new AttributionGraph(s2).Build(targetToken: 99);
            Assert.Empty(r.Nodes);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static TransactionEvent Logits(string tx, ulong eid, int tok)
    {
        byte[] p = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(p, 0);
        return new TransactionEvent(eid, tx, (long)eid, EventKind.Logits,
            tok, -1, -1, 0, 0, 0, p, Array.Empty<TraceMarker>());
    }

    private static TransactionEvent AttentionScores(string tx, ulong eid, int tok, int layer, int head, float maxProb)
    {
        // Payload from pack_head_stats: (head:i32, mean:f32, std:f32, entropy:f32, max_prob:f32, argmax:i32)
        byte[] p = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(0),  head);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(4), 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(8), 0.1f);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(12), 1.0f);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(16), maxProb);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(20), 0);
        return new TransactionEvent(eid, tx, (long)eid, EventKind.AttentionScores,
            tok, layer, head, 0, 0, 0, p, Array.Empty<TraceMarker>());
    }
}
