using System.Buffers.Binary;
using StackScope.Core.Analysis;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;
using Xunit;

namespace StackScope.Core.Tests;

public class DeterminismAuditorTests
{
    [Fact]
    public void Reports_No_Findings_When_Content_Identical()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ss-da-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string a = Ulid.NewUlid(), b = Ulid.NewUlid();
        try
        {
            using (var A = new EventStore(a, dir))
            using (var B = new EventStore(b, dir))
            {
                for (int i = 0; i < 4; i++)
                {
                    A.Append(Logits(a, (ulong)(100 + i), tok: i, val: 0.5f + i));
                    B.Append(Logits(b, (ulong)(200 + i), tok: i, val: 0.5f + i));
                }
            }
            using var A2 = new EventStore(a, dir);
            using var B2 = new EventStore(b, dir);
            var r = new DeterminismAuditor(A2, B2).Audit();
            Assert.True(r.Deterministic);
            Assert.Empty(r.Findings);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Finds_First_Nondeterministic_Event()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ss-da-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string a = Ulid.NewUlid(), b = Ulid.NewUlid();
        try
        {
            using (var A = new EventStore(a, dir))
            using (var B = new EventStore(b, dir))
            {
                for (int i = 0; i < 4; i++)
                {
                    A.Append(Logits(a, (ulong)(100 + i), tok: i, val: 0.5f + i));
                    // First 2 identical; 3rd diverges
                    float valB = i == 2 ? 999f : 0.5f + i;
                    B.Append(Logits(b, (ulong)(200 + i), tok: i, val: valB));
                }
            }
            using var A2 = new EventStore(a, dir);
            using var B2 = new EventStore(b, dir);
            var r = new DeterminismAuditor(A2, B2).Audit();
            Assert.False(r.Deterministic);
            Assert.NotEmpty(r.Findings);
            Assert.Equal(EventKind.Logits, r.Findings[0].Kind);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static TransactionEvent Logits(string tx, ulong eid, int tok, float val)
    {
        byte[] p = new byte[4 + 8];
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(0), 1);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4), 0);
        BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(8), val);
        return new TransactionEvent(eid, tx, (long)eid, EventKind.Logits,
            tok, 0, -1, 0, 0, 0, p, Array.Empty<TraceMarker>());
    }
}
