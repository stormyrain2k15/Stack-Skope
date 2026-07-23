using System.Buffers.Binary;
using StackScope.Core.Analysis;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;
using Xunit;

namespace StackScope.Core.Tests;

public class DivergenceDetectorTests
{
    [Fact]
    public void Detects_First_Divergent_Token()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ss-div-" + Guid.NewGuid().ToString("N"));
        string txA = Ulid.NewUlid();
        string txB = Ulid.NewUlid();
        Directory.CreateDirectory(dir);

        try
        {
            using (var A = new EventStore(txA, dir))
            using (var B = new EventStore(txB, dir))
            {
                for (int t = 0; t < 6; t++)
                {
                    int idA = t;
                    int idB = t == 3 ? 99 : t;
                    A.Append(TokenEnd(txA, (ulong)t, t, idA));
                    B.Append(TokenEnd(txB, (ulong)t, t, idB));
                }
            }

            using var A2 = new EventStore(txA, dir);
            using var B2 = new EventStore(txB, dir);
            var det = new DivergenceDetector(new[] { A2, B2 });
            var r = det.Detect(sigmaThreshold: 100.0);
            Assert.Equal(3, r.FirstDivergentToken);
            Assert.Equal(new[] { 3, 99 }, r.SampledIdsPerRun);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static TransactionEvent TokenEnd(string txid, ulong id, int tok, int sampledId)
    {
        byte[] p = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(0), sampledId);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(4), 0.5f);
        return new TransactionEvent(id, txid, (long)id * 100, EventKind.TokenEnd,
            tok, -1, -1, 1, 0, 0, p, Array.Empty<TraceMarker>());
    }
}
