using System.Buffers.Binary;
using StackScope.Core.Comparison;
using StackScope.Core.Queries;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;

namespace StackScope.Core.Analysis;

/// <summary>
/// Quantization diff. Given two runs of the same prompt at different
/// numeric precisions (typically f16 vs a quantized scheme like
/// q4_k_m), reports where they diverge — per-layer distribution shift,
/// first sampled-token divergence, and the total energy of the delta
/// across the whole capture.
///
/// Sibling to <see cref="DivergenceDetector"/>, but the "axis" here is
/// quantization scheme rather than seed.
/// </summary>
public sealed class QuantizationDiff
{
    public sealed record LayerShift(int Layer, double SigmaShift, double CosineSimilarity);
    public sealed record Result(
        string ReferenceQuant,
        string CandidateQuant,
        int FirstDivergentToken,
        int FirstDivergentLayer,
        double TotalEnergyDelta,
        IReadOnlyList<LayerShift> PerLayer);

    private readonly EventStore _reference;
    private readonly EventStore _candidate;
    private readonly string _refName;
    private readonly string _candName;

    public QuantizationDiff(EventStore reference, EventStore candidate,
        string referenceQuant = "f16", string candidateQuant = "q4_k_m")
    {
        _reference = reference;
        _candidate = candidate;
        _refName   = referenceQuant;
        _candName  = candidateQuant;
    }

    public Result Compute(int maxLayers = 128)
    {
        var qr = new QueryEngine(_reference);
        var qc = new QueryEngine(_candidate);

        // First divergent sampled token.
        var idsRef  = SampledIds(qr);
        var idsCand = SampledIds(qc);
        int firstToken = -1;
        int n = Math.Min(idsRef.Count, idsCand.Count);
        for (int t = 0; t < n; t++)
            if (idsRef[t] != idsCand[t]) { firstToken = t; break; }

        // Per-layer distribution shift.
        var perLayer = new List<LayerShift>();
        int firstDivLayer = -1;
        double totalEnergy = 0.0;
        int target = firstToken >= 0 ? firstToken : 0;

        for (int layer = 0; layer < maxLayers; layer++)
        {
            var refStats  = LogitsAt(qr, target, layer);
            var candStats = LogitsAt(qc, target, layer);
            if (refStats.N < 2 && candStats.N < 2) continue;

            double sigma = candStats.SigmaShift(refStats);
            double cos   = candStats.CosineToward(refStats);
            perLayer.Add(new LayerShift(layer, sigma, cos));

            totalEnergy += Math.Abs(refStats.AbsEnergy - candStats.AbsEnergy);

            if (sigma >= 1.0 && firstDivLayer == -1) firstDivLayer = layer;
        }

        return new Result(_refName, _candName, firstToken, firstDivLayer, totalEnergy, perLayer);
    }

    private static List<int> SampledIds(QueryEngine qe)
    {
        var xs = new List<(int tok, int id)>();
        foreach (var e in qe.Query(new EventQuery { Kinds = new[] { EventKind.TokenEnd }, Limit = 1_000_000 }))
        {
            if (e.Payload.Length < 4) continue;
            int id = BinaryPrimitives.ReadInt32LittleEndian(e.Payload.Span);
            xs.Add((e.TokenIndex, id));
        }
        xs.Sort((a, b) => a.tok.CompareTo(b.tok));
        return xs.Select(x => x.id).ToList();
    }

    private static DistributionStats LogitsAt(QueryEngine qe, int token, int layer)
    {
        var s = new DistributionStats();
        foreach (var e in qe.Query(new EventQuery
        {
            Kinds = new[] { EventKind.Logits },
            TokenIndex = new IntRange(token, token),
            LayerIndex = layer == 0 ? IntRange.All : new IntRange(layer, layer),
            Limit = 4096,
        }))
        {
            foreach (var v in EventPayload.LogitsFrom(e.Payload)) s.Push(v);
        }
        return s;
    }
}
