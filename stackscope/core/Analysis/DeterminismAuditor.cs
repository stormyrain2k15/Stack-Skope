using System.Buffers.Binary;
using System.Security.Cryptography;
using StackScope.Core.Queries;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;

namespace StackScope.Core.Analysis;

/// <summary>
/// Determinism auditor. Runs the same input twice; reports which
/// kernel first introduced non-determinism.
///
/// The signal is a per-event content hash (kind + payload) grouped
/// by (correlation_id, layer). Two runs that were deterministic hash
/// identically for the same event stream up to the first divergent
/// kernel launch.
/// </summary>
public sealed class DeterminismAuditor
{
    public sealed record Finding(
        int LayerIndex,
        EventKind Kind,
        ulong CorrelationId,
        string MarkerName,
        string HashA,
        string HashB);

    public sealed record Result(bool Deterministic, IReadOnlyList<Finding> Findings);

    private readonly EventStore _a;
    private readonly EventStore _b;

    public DeterminismAuditor(EventStore a, EventStore b) { _a = a; _b = b; }

    public Result Audit(int maxFindings = 32)
    {
        var qa = new QueryEngine(_a);
        var qb = new QueryEngine(_b);
        // Only kernel-launch / kernel-end / attention / logits events matter
        // for determinism — token / layer boundaries are latency-only.
        var kinds = new[]
        {
            EventKind.KernelLaunch, EventKind.KernelEnd,
            EventKind.AttentionQkv, EventKind.AttentionScores,
            EventKind.AttentionOutput, EventKind.Logits, EventKind.Sample,
            EventKind.TensorRead, EventKind.TensorWrite,
        };
        var q = new EventQuery { Kinds = kinds, Limit = int.MaxValue };
        using var ea = qa.Query(q).GetEnumerator();
        using var eb = qb.Query(q).GetEnumerator();

        var findings = new List<Finding>();
        while (ea.MoveNext() && eb.MoveNext())
        {
            var a = ea.Current; var b = eb.Current;
            if (a.Kind != b.Kind || a.LayerIndex != b.LayerIndex)
            {
                findings.Add(new Finding(a.LayerIndex, a.Kind,
                    MarkerCorr(a), MarkerName(a), Hash(a), Hash(b)));
            }
            else
            {
                var ha = Hash(a);
                var hb = Hash(b);
                if (!ha.Equals(hb, StringComparison.Ordinal))
                {
                    findings.Add(new Finding(a.LayerIndex, a.Kind,
                        MarkerCorr(a), MarkerName(a), ha, hb));
                }
            }
            if (findings.Count >= maxFindings) break;
        }
        return new Result(findings.Count == 0, findings);
    }

    private static string Hash(TransactionEvent e)
    {
        // Content-stable hash. Excludes wall-clock ts_ns and thread id.
        Span<byte> header = stackalloc byte[9];
        header[0] = (byte)e.Kind;
        BinaryPrimitives.WriteInt32LittleEndian(header[1..], e.LayerIndex);
        BinaryPrimitives.WriteInt32LittleEndian(header[5..], e.HeadIndex);
        var sha = SHA256.HashData(header.ToArray().Concat(e.Payload.ToArray()).ToArray());
        return Convert.ToHexString(sha, 0, 8);
    }

    private static ulong MarkerCorr(TransactionEvent e)
        => e.Markers.Count > 0 ? e.Markers[0].CorrelationId : 0UL;

    private static string MarkerName(TransactionEvent e)
        => e.Markers.Count > 0 ? e.Markers[0].Name : "";
}
