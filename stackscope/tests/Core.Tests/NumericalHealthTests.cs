using System.Buffers.Binary;
using System.Text;
using StackScope.Core.Analysis;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;
using Xunit;

namespace StackScope.Core.Tests;

public class NumericalHealthTests
{
    [Fact]
    public void Aggregates_Anomaly_Markers_And_Latencies()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ss-nh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string tx = Ulid.NewUlid();
        try
        {
            using (var s = new EventStore(tx, dir))
            {
                ulong eid = 0;
                // Two NaN anomaly markers on layer 3
                s.Append(NanMarker(tx, ++eid, layer: 3));
                s.Append(NanMarker(tx, ++eid, layer: 3));
                // One entropy-collapse anomaly on layer 5
                s.Append(CollapseMarker(tx, ++eid, layer: 5));
                // Two LayerEnd events on layer 3 with 1ms and 2ms latency
                s.Append(LayerEnd(tx, ++eid, layer: 3, durNs: 1_000_000));
                s.Append(LayerEnd(tx, ++eid, layer: 3, durNs: 2_000_000));
            }

            using var s2 = new EventStore(tx, dir);
            var r = new NumericalHealth(s2).Compute();
            Assert.Equal(5, r.TotalEvents);
            Assert.Equal(3, r.TotalAnomalies);

            var l3 = r.Layers.Single(x => x.Layer == 3);
            Assert.Equal(2, l3.NanInfCount);
            Assert.InRange(l3.LayerMedianMs, 1.0, 2.0);

            var l5 = r.Layers.Single(x => x.Layer == 5);
            Assert.Equal(1, l5.EntropyCollapseCount);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static TransactionEvent NanMarker(string tx, ulong eid, int layer)
    {
        var p = Encoding.UTF8.GetBytes($"nan-or-inf-logit@k0=v=NaN");
        var m = new TraceMarker("stackscope.anomaly", 0, 0, 0, 0, 0, 0);
        return new TransactionEvent(eid, tx, (long)eid * 10, EventKind.Marker,
            -1, layer, -1, 0, 0, 0, p, new[] { m });
    }

    private static TransactionEvent CollapseMarker(string tx, ulong eid, int layer)
    {
        var p = Encoding.UTF8.GetBytes($"attention-entropy-collapse head=0 entropy=0.02");
        var m = new TraceMarker("stackscope.anomaly", 0, 0, 0, 0, 0, 0);
        return new TransactionEvent(eid, tx, (long)eid * 10, EventKind.Marker,
            -1, layer, -1, 0, 0, 0, p, new[] { m });
    }

    private static TransactionEvent LayerEnd(string tx, ulong eid, int layer, long durNs)
    {
        long begin = (long)eid * 10_000_000;
        long end = begin + durNs;
        var m = new TraceMarker($"layer.{layer}", begin, end, 0, 0, 0, 0);
        return new TransactionEvent(eid, tx, end, EventKind.LayerEnd,
            0, layer, -1, 0, 0, 0, Array.Empty<byte>(), new[] { m });
    }
}
