using System.Buffers.Binary;
using StackScope.Core.Comparison;
using StackScope.Core.Queries;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;

namespace StackScope.Core.Analysis;

/// <summary>
/// Numerical health dashboard.
///
/// Walks the capture, aggregating anomaly markers, NaN/Inf counts,
/// entropy statistics, and per-layer wall time. Answers the "is
/// this model actually producing sane numbers?" question at a glance.
///
/// Emits a per-layer summary and a top-level roll-up. Pure function
/// over an EventStore — no side effects, safe to re-run.
/// </summary>
public sealed class NumericalHealth
{
    public sealed record LayerHealth(
        int Layer,
        int NanInfCount,
        int EntropyCollapseCount,
        int EntropyDegenerateCount,
        int LatencyOutlierCount,
        double MinEntropy,
        double MaxEntropy,
        double LayerMedianMs);

    public sealed record Report(
        int TotalEvents,
        int TotalAnomalies,
        IReadOnlyList<LayerHealth> Layers,
        IReadOnlyList<string> Findings);

    private readonly EventStore _store;

    public NumericalHealth(EventStore store) { _store = store; }

    public Report Compute()
    {
        var qe = new QueryEngine(_store);
        var byLayer = new Dictionary<int, LayerAcc>();
        var latencies = new Dictionary<int, List<double>>();
        int totalEvents = 0;
        int totalAnomalies = 0;
        var findings = new List<string>();

        foreach (var e in qe.Query(new EventQuery { Limit = int.MaxValue }))
        {
            totalEvents++;
            int layer = e.LayerIndex < 0 ? -1 : e.LayerIndex;
            var acc = byLayer.TryGetValue(layer, out var a) ? a : (byLayer[layer] = new LayerAcc());

            if (e.Kind == EventKind.Marker && e.Markers.Count > 0)
            {
                // Anomaly markers have name "stackscope.anomaly".
                foreach (var m in e.Markers)
                {
                    if (m.Name.Equals("stackscope.anomaly", StringComparison.Ordinal))
                    {
                        totalAnomalies++;
                        var desc = System.Text.Encoding.UTF8.GetString(e.Payload.Span);
                        if (desc.Contains("nan", StringComparison.OrdinalIgnoreCase)
                         || desc.Contains("inf", StringComparison.OrdinalIgnoreCase))
                            acc.NanInf++;
                        else if (desc.Contains("collapse", StringComparison.OrdinalIgnoreCase))
                            acc.Collapse++;
                        else if (desc.Contains("degenerate", StringComparison.OrdinalIgnoreCase))
                            acc.Degenerate++;
                        else if (desc.Contains("latency-outlier", StringComparison.OrdinalIgnoreCase))
                            acc.LatencyOutliers++;
                        findings.Add($"L{layer:D2} {desc}");
                    }
                }
            }
            else if (e.Kind == EventKind.AttentionScores && e.Payload.Length >= 24)
            {
                // Attention head stats: (head:i32, mean:f32, std:f32, entropy:f32, ...)
                var s = e.Payload.Span;
                float entropy = BinaryPrimitives.ReadSingleLittleEndian(s.Slice(12, 4));
                if (entropy < acc.MinEntropy) acc.MinEntropy = entropy;
                if (entropy > acc.MaxEntropy) acc.MaxEntropy = entropy;
            }
            else if (e.Kind == EventKind.LayerEnd && layer >= 0 && e.Markers.Count > 0)
            {
                var m = e.Markers[0];
                if (m.EndNs > m.BeginNs)
                {
                    double durMs = (m.EndNs - m.BeginNs) / 1e6;
                    if (!latencies.TryGetValue(layer, out var xs))
                        latencies[layer] = xs = new List<double>();
                    xs.Add(durMs);
                }
            }
        }

        var layers = byLayer
            .Where(kv => kv.Key >= 0)
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                double med = 0.0;
                if (latencies.TryGetValue(kv.Key, out var xs) && xs.Count > 0)
                {
                    xs.Sort();
                    med = xs[xs.Count / 2];
                }
                return new LayerHealth(
                    kv.Key, kv.Value.NanInf, kv.Value.Collapse,
                    kv.Value.Degenerate, kv.Value.LatencyOutliers,
                    kv.Value.MinEntropy == double.PositiveInfinity ? 0 : kv.Value.MinEntropy,
                    kv.Value.MaxEntropy == double.NegativeInfinity ? 0 : kv.Value.MaxEntropy,
                    med);
            })
            .ToList();

        return new Report(totalEvents, totalAnomalies, layers, findings);
    }

    private sealed class LayerAcc
    {
        public int NanInf;
        public int Collapse;
        public int Degenerate;
        public int LatencyOutliers;
        public double MinEntropy = double.PositiveInfinity;
        public double MaxEntropy = double.NegativeInfinity;
    }
}
