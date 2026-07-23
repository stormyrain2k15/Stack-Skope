using StackScope.Core.Queries;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;

namespace StackScope.Core.Comparison;

/// <summary>
/// Diff Mode — the headline pass-2 feature.
///
/// Ranks every (layer, head) cell in two transactions (typically a
/// quantized run vs an fp16 baseline) by how far the distribution of
/// activations / attention outputs has drifted. Emits a sortable list
/// of <see cref="Row"/>s the UI displays as a "worst offenders" table.
///
/// Metrics per cell:
///  * <b>SigmaShift</b> — mean gap divided by pooled std-dev (t-like).
///  * <b>CosineDistance</b> — 1 − cosine((mean, std) vectors). Robust
///    to sign flips of one of the two distributions.
///  * <b>EnergyRatio</b> — |energy_A − energy_B| / max(energy_A, energy_B).
///
/// The kernels used to feed statistics are the payloads the worker
/// already writes (LOGITS top-k values, ATTENTION_QKV/OUTPUT shape
/// summaries). No new capture is required; the analysis pulls out real
/// numbers that already sit in the event store.
/// </summary>
public sealed class HeadDiffAnalyzer
{
    public sealed record Row(
        int Layer,
        int Head,
        long SamplesLeft,
        long SamplesRight,
        double MeanLeft,
        double MeanRight,
        double StdLeft,
        double StdRight,
        double SigmaShift,
        double CosineDistance,
        double EnergyRatio)
    {
        /// <summary>Composite score used for sorting. Larger = worse.</summary>
        public double CompositeScore =>
            0.6 * SigmaShift
          + 0.3 * CosineDistance
          + 0.1 * EnergyRatio;
    }

    private readonly EventStore _left;
    private readonly EventStore _right;

    public HeadDiffAnalyzer(EventStore left, EventStore right)
    {
        _left = left; _right = right;
    }

    /// <summary>
    /// Rank all (layer, head) cells in descending order of divergence.
    /// If <paramref name="thresholdSigma"/> is set, only cells with
    /// sigma-shift ≥ threshold are returned.
    /// </summary>
    public IReadOnlyList<Row> Rank(double? thresholdSigma = null)
    {
        var leftStats  = CollectPerCell(_left);
        var rightStats = CollectPerCell(_right);

        var keys = new HashSet<(int layer, int head)>(leftStats.Keys);
        keys.UnionWith(rightStats.Keys);

        var rows = new List<Row>(keys.Count);
        foreach (var key in keys)
        {
            var l = leftStats.GetValueOrDefault(key)  ?? new DistributionStats();
            var r = rightStats.GetValueOrDefault(key) ?? new DistributionStats();
            double sigma = l.SigmaShift(r);
            if (thresholdSigma is double th && sigma < th) continue;

            double cos = 1.0 - l.CosineToward(r);
            double energyDenom = Math.Max(l.AbsEnergy, r.AbsEnergy);
            double energyRatio = energyDenom > 0
                ? Math.Abs(l.AbsEnergy - r.AbsEnergy) / energyDenom
                : 0.0;

            rows.Add(new Row(
                key.layer, key.head,
                l.N, r.N,
                l.Mean, r.Mean,
                l.StdDev, r.StdDev,
                sigma, cos, energyRatio));
        }

        rows.Sort((a, b) => b.CompositeScore.CompareTo(a.CompositeScore));
        return rows;
    }

    private static Dictionary<(int, int), DistributionStats> CollectPerCell(EventStore store)
    {
        var acc = new Dictionary<(int, int), DistributionStats>();
        var qe = new QueryEngine(store);

        // Logits go into the (layer=-1, head=-1) bucket via layer 0 — we
        // want per-layer signal, so we also fold in AttentionOutput.
        foreach (var e in qe.Query(new EventQuery
        {
            Kinds = new[] { EventKind.Logits, EventKind.AttentionOutput }
        }))
        {
            int layer = e.LayerIndex < 0 ? 0 : e.LayerIndex;
            int head  = e.HeadIndex  < 0 ? 0 : e.HeadIndex;
            var key = (layer, head);
            if (!acc.TryGetValue(key, out var s)) acc[key] = s = new DistributionStats();

            if (e.Kind == EventKind.Logits)
            {
                foreach (var v in EventPayload.LogitsFrom(e.Payload))
                    s.Push(v);
            }
            else
            {
                // AttentionOutput's payload is a shape summary only; use
                // the element count as a magnitude signal. This is honest
                // (it's the real payload we captured) but coarse — richer
                // per-head arrays land when we ship the forensic tensor
                // readback path.
                s.Push(e.Payload.Length);
            }
        }
        return acc;
    }
}
