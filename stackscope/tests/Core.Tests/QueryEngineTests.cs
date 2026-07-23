using StackScope.Core.Queries;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;
using Xunit;

namespace StackScope.Core.Tests;

public class QueryEngineTests
{
    private static string SeedStore(out EventStore store, out string tmpDir)
    {
        tmpDir = Path.Combine(Path.GetTempPath(), "ss-query-" + Guid.NewGuid().ToString("N"));
        var txid = Ulid.NewUlid();
        store = new EventStore(txid, tmpDir);

        // 3 tokens × 4 layers × (LayerBegin, LayerEnd) + a few KernelLaunch.
        ulong id = 0;
        for (int tok = 0; tok < 3; tok++)
        for (int lay = 0; lay < 4; lay++)
        {
            store.Append(new TransactionEvent(id++, txid, 100 * (long)id, EventKind.LayerBegin,
                tok, lay, -1, 1, 0, 0, Array.Empty<byte>(), Array.Empty<TraceMarker>()));
            store.Append(new TransactionEvent(id++, txid, 100 * (long)id, EventKind.LayerEnd,
                tok, lay, -1, 1, 0, 0, Array.Empty<byte>(), Array.Empty<TraceMarker>()));
        }
        for (int k = 0; k < 8; k++)
            store.Append(new TransactionEvent(id++, txid, 100 * (long)id, EventKind.KernelLaunch,
                -1, -1, -1, 1, 0, 0, Array.Empty<byte>(), Array.Empty<TraceMarker>()));
        store.Flush();
        return txid;
    }

    [Fact]
    public void Filters_By_Kind_And_Token()
    {
        SeedStore(out var store, out var dir);
        try
        {
            var qe = new QueryEngine(store);
            var q = new EventQuery
            {
                Kinds = new[] { EventKind.LayerBegin },
                TokenIndex = new IntRange(1, 1)
            };
            var results = qe.Query(q).ToList();
            Assert.Equal(4, results.Count);
            Assert.All(results, e => Assert.Equal(1, e.TokenIndex));
            Assert.All(results, e => Assert.Equal(EventKind.LayerBegin, e.Kind));
        }
        finally { store.Dispose(); try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Count_Matches_Query()
    {
        SeedStore(out var store, out var dir);
        try
        {
            var qe = new QueryEngine(store);
            var q = new EventQuery { Kinds = new[] { EventKind.KernelLaunch } };
            Assert.Equal(8, qe.Count(q));
        }
        finally { store.Dispose(); try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Paging_Respects_Offset_And_Limit()
    {
        SeedStore(out var store, out var dir);
        try
        {
            var qe = new QueryEngine(store);
            var page1 = qe.Query(new EventQuery { Limit = 5, Offset = 0 }).ToList();
            var page2 = qe.Query(new EventQuery { Limit = 5, Offset = 5 }).ToList();
            Assert.Equal(5, page1.Count);
            Assert.Equal(5, page2.Count);
            Assert.All(page1.Zip(page2, (a, b) => a.EventId < b.EventId), Assert.True);
        }
        finally { store.Dispose(); try { Directory.Delete(dir, true); } catch { } }
    }
}
