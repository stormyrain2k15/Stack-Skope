using StackScope.Core.Comparison;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;
using Xunit;

namespace StackScope.Core.Tests;

public class HeadDiffAnalyzerTests
{
    /// <summary>
    /// End-to-end: build two fake transactions with LOGITS payloads
    /// that differ only in a specific (layer, head) cell, run the
    /// analyzer, and assert the outlier cell tops the ranking.
    /// </summary>
    [Fact]
    public void Worst_Offender_Cell_Is_Ranked_First()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ss-diff-" + Guid.NewGuid().ToString("N"));
        string txA = Ulid.NewUlid();
        string txB = Ulid.NewUlid();
        Directory.CreateDirectory(dir);

        try
        {
            using (var A = new EventStore(txA, dir))
            using (var B = new EventStore(txB, dir))
            {
                ulong id = 0;
                for (int layer = 0; layer < 4; layer++)
                for (int head  = 0; head  < 4; head++)
                {
                    // Baseline: same distribution both sides everywhere
                    // EXCEPT the outlier cell (2,3), which we bump on B.
                    for (int i = 0; i < 20; i++)
                    {
                        float v = 0.1f * i;
                        A.Append(MakeLogits(txA, id++, layer, head, v));
                        float outlier = (layer == 2 && head == 3) ? v + 5.0f : v;
                        B.Append(MakeLogits(txB, id++, layer, head, outlier));
                    }
                }
            }

            using (var A = new EventStore(txA, dir))
            using (var B = new EventStore(txB, dir))
            {
                var analyzer = new HeadDiffAnalyzer(A, B);
                var ranking = analyzer.Rank(thresholdSigma: null);

                Assert.NotEmpty(ranking);
                var top = ranking[0];
                Assert.Equal(2, top.Layer);
                Assert.Equal(3, top.Head);
                Assert.True(top.SigmaShift > 1.0,
                    $"Expected large sigma shift, got {top.SigmaShift:F3}");
            }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static TransactionEvent MakeLogits(string txid, ulong id,
                                               int layer, int head, float val)
    {
        // Payload layout matches EventPayload.LogitsFrom expectations:
        //   [ i32 k=1 ][ i32 token_id ][ f32 value ]
        byte[] payload = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(0), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(4), 0);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(8), val);
        return new TransactionEvent(
            id, txid, (long)id, EventKind.Logits,
            /*token*/ 0, layer, head, 1, 0, 0,
            payload, Array.Empty<TraceMarker>());
    }
}
