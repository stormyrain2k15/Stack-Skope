using StackScope.Core.Storage;
using StackScope.Core.Transactions;
using Xunit;

namespace StackScope.Core.Tests;

public class EventStoreRoundTripTests
{
    [Fact]
    public void Appends_Then_Reads_Back_All_Events()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ss-store-" + Guid.NewGuid().ToString("N"));
        string txid = Ulid.NewUlid();
        try
        {
            using (var store = new EventStore(txid, dir))
            {
                for (int i = 0; i < 100; i++)
                {
                    var e = new TransactionEvent(
                        EventId: (ulong)i,
                        TransactionId: txid,
                        TimestampNs: 1_000_000 * i,
                        Kind: (EventKind)((i % 5) + 1),
                        TokenIndex: i / 10,
                        LayerIndex: i % 32,
                        HeadIndex: i % 8,
                        ThreadId: 1, StreamId: 0, DeviceId: 0,
                        Payload: new byte[] { (byte)i, (byte)(i + 1) },
                        Markers: new[]
                        {
                            new TraceMarker($"m{i}", 100 + i, 200 + i, 0xFF808080, 1, 0, (ulong)(i + 1))
                        });
                    store.Append(e);
                }
                store.Flush();
            }

            using (var store = new EventStore(txid, dir))
            {
                // Read back all offsets from SQLite and dereference each.
                var offsets = new List<long>();
                using var cmd = store.Index.Connection.CreateCommand();
                cmd.CommandText = "SELECT log_offset FROM events ORDER BY event_id;";
                using var r = cmd.ExecuteReader();
                while (r.Read()) offsets.Add(r.GetInt64(0));

                Assert.Equal(100, offsets.Count);

                for (int i = 0; i < 100; i++)
                {
                    var e = store.ReadAt(offsets[i]);
                    Assert.Equal((ulong)i, e.EventId);
                    Assert.Equal(txid, e.TransactionId);
                    Assert.Equal(1_000_000L * i, e.TimestampNs);
                    Assert.Equal((EventKind)((i % 5) + 1), e.Kind);
                    Assert.Equal(i / 10, e.TokenIndex);
                    Assert.Equal(i % 32, e.LayerIndex);
                    Assert.Equal(i % 8,  e.HeadIndex);
                    Assert.Equal(2, e.Payload.Length);
                    Assert.Equal((byte)i, e.Payload.Span[0]);
                    Assert.Single(e.Markers);
                    Assert.Equal($"m{i}", e.Markers[0].Name);
                    Assert.Equal((ulong)(i + 1), e.Markers[0].CorrelationId);
                }
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Bulk_Append_Preserves_Order()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ss-bulk-" + Guid.NewGuid().ToString("N"));
        string txid = Ulid.NewUlid();
        try
        {
            var events = Enumerable.Range(0, 500).Select(i =>
                new TransactionEvent(
                    (ulong)i, txid, i * 1_000L,
                    EventKind.LayerBegin, i / 50, i, -1,
                    1, 0, 0,
                    Array.Empty<byte>(), Array.Empty<TraceMarker>())).ToList();

            using (var store = new EventStore(txid, dir))
            {
                store.AppendMany(events);
                store.Flush();
            }

            using (var store = new EventStore(txid, dir))
            {
                using var cmd = store.Index.Connection.CreateCommand();
                cmd.CommandText = "SELECT event_id FROM events ORDER BY event_id;";
                using var r = cmd.ExecuteReader();
                int expected = 0;
                while (r.Read())
                {
                    Assert.Equal((long)expected, r.GetInt64(0));
                    expected++;
                }
                Assert.Equal(500, expected);
            }
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
