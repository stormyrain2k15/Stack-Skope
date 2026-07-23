using System.Buffers.Binary;
using StackScope.Core.Comparison;
using StackScope.Core.Queries;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;

namespace StackScope.Core.Analysis;

/// <summary>
/// Given N runs of the same prompt (usually different seeds), finds
/// the first token where the sampled ids diverged, and the earliest
/// layer where the underlying distributions had already drifted past
/// a sigma threshold.
///
/// The event store already contains everything we need — no re-run.
/// </summary>
public sealed class DivergenceDetector
{
    public sealed record Result(
        int FirstDivergentToken,
        int FirstDivergentLayer,
        double MaxSigmaAtDivergence,
        IReadOnlyList<int> SampledIdsPerRun);

    private readonly IReadOnlyList<EventStore> _runs;

    public DivergenceDetector(IReadOnlyList<EventStore> runs)
    {
        if (runs.Count < 2) throw new ArgumentException("Need ≥2 runs.", nameof(runs));
        _runs = runs;
    }

    public Result Detect(double sigmaThreshold = 1.0)
    {
        var samplesPerRun = _runs.Select(SampleIdsByToken).ToList();
        int minLen = samplesPerRun.Min(s => s.Count);
        int firstToken = -1;
        for (int t = 0; t < minLen; t++)
        {
            var first = samplesPerRun[0][t];
            if (samplesPerRun.Any(s => s[t] != first)) { firstToken = t; break; }
        }
        if (firstToken < 0)
            return new Result(-1, -1, 0.0, Array.Empty<int>());

        var idsAtT = samplesPerRun.Select(s => s[firstToken]).ToArray();

        var qA = new QueryEngine(_runs[0]);
        var qB = new QueryEngine(_runs[1]);

        int firstLayer = -1;
        double maxSigma = 0.0;
        for (int layer = 0; layer < 128; layer++)
        {
            var la = LogitsAtTokenLayer(qA, firstToken, layer);
            var lb = LogitsAtTokenLayer(qB, firstToken, layer);
            if (la.N < 2 || lb.N < 2) continue;
            double sigma = la.SigmaShift(lb);
            if (sigma > maxSigma) maxSigma = sigma;
            if (sigma >= sigmaThreshold && firstLayer == -1) firstLayer = layer;
            if (la.N == 0 && lb.N == 0) break;
        }
        return new Result(firstToken, firstLayer, maxSigma, idsAtT);
    }

    private static List<int> SampleIdsByToken(EventStore s)
    {
        var qe = new QueryEngine(s);
        var xs = new List<(int tok, int id)>();
        foreach (var e in qe.Query(new EventQuery
        {
            Kinds = new[] { EventKind.TokenEnd }, Limit = 1_000_000
        }))
        {
            if (e.Payload.Length < 4) continue;
            int id = BinaryPrimitives.ReadInt32LittleEndian(e.Payload.Span);
            xs.Add((e.TokenIndex, id));
        }
        xs.Sort((a, b) => a.tok.CompareTo(b.tok));
        return xs.Select(x => x.id).ToList();
    }

    private static DistributionStats LogitsAtTokenLayer(QueryEngine qe, int token, int layer)
    {
        var s = new DistributionStats();
        foreach (var e in qe.Query(new EventQuery
        {
            Kinds = new[] { EventKind.Logits, EventKind.AttentionScores },
            TokenIndex = new IntRange(token, token),
            LayerIndex = layer == 0 ? IntRange.All : new IntRange(layer, layer),
            Limit = 4096
        }))
        {
            if (e.Kind == EventKind.Logits)
                foreach (var v in EventPayload.LogitsFrom(e.Payload)) s.Push(v);
            else if (e.Payload.Length >= 24)
            {
                var p = e.Payload.Span;
                s.Push(BinaryPrimitives.ReadSingleLittleEndian(p[4..]));  // mean
                s.Push(BinaryPrimitives.ReadSingleLittleEndian(p[12..])); // entropy
            }
        }
        return s;
    }
}
