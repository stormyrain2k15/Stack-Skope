using System.Buffers.Binary;
using StackScope.Core.Analysis;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;
using Xunit;

namespace StackScope.Core.Tests;

public class QuantizationDiffTests
{
    [Fact]
    public void Finds_First_Divergent_Token_And_Layer()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ss-qd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string refId = Ulid.NewUlid();
        string candId = Ulid.NewUlid();
        try
        {
            using (var refStore = new EventStore(refId, dir))
            using (var candStore = new EventStore(candId, dir))
            {
                for (int t = 0; t < 4; t++)
                {
                    int refSampled = t;
                    int candSampled = t == 2 ? 999 : t;
                    refStore.Append(TokenEnd(refId, (ulong)(100 + t), t, refSampled));
                    candStore.Append(TokenEnd(candId, (ulong)(200 + t), t, candSampled));
                }
                // Different logits at token=2, layer=0
                refStore.Append(Logits(refId, 300, 2, mean: 1.0f));
                candStore.Append(Logits(candId, 301, 2, mean: 3.0f));
            }

            using var refS = new EventStore(refId, dir);
            using var candS = new EventStore(candId, dir);
            var r = new QuantizationDiff(refS, candS, "f16", "q4").Compute();
            Assert.Equal(2, r.FirstDivergentToken);
            Assert.Equal("f16", r.ReferenceQuant);
            Assert.Equal("q4", r.CandidateQuant);
            Assert.True(r.TotalEnergyDelta >= 0);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static TransactionEvent TokenEnd(string tx, ulong eid, int tok, int sampled)
    {
        byte[] p = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(0), sampled);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(4), 0.5f);
        return new TransactionEvent(eid, tx, (long)eid, EventKind.TokenEnd,
            tok, -1, -1, 0, 0, 0, p, Array.Empty<TraceMarker>());
    }

    private static TransactionEvent Logits(string tx, ulong eid, int tok, float mean)
    {
        // Payload: [i32 k=4][ (i32 id, f32 val) * 4 ] — vals cluster around `mean`
        byte[] p = new byte[4 + 4 * 8];
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(0), 4);
        for (int i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4 + i * 8), i);
            BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(8 + i * 8), mean + i * 0.1f);
        }
        return new TransactionEvent(eid, tx, (long)eid, EventKind.Logits,
            tok, 0, -1, 0, 0, 0, p, Array.Empty<TraceMarker>());
    }
}
