using System.Buffers.Binary;
using StackScope.Core.Transactions;

namespace StackScope.Core.Comparison;

/// <summary>
/// Streaming statistics over a scalar sequence — mean, variance,
/// min, max, absolute-value energy. Uses Welford's algorithm so it's
/// stable across large samples.
/// </summary>
public sealed class DistributionStats
{
    public long   N        { get; private set; }
    public double Mean     { get; private set; }
    public double M2       { get; private set; }   // sum of squared deviations
    public double Min      { get; private set; } = double.PositiveInfinity;
    public double Max      { get; private set; } = double.NegativeInfinity;
    public double AbsEnergy{ get; private set; }

    public double Variance => N > 1 ? M2 / (N - 1) : 0.0;
    public double StdDev   => Math.Sqrt(Variance);

    public void Push(double x)
    {
        N++;
        double delta = x - Mean;
        Mean += delta / N;
        double delta2 = x - Mean;
        M2 += delta * delta2;
        if (x < Min) Min = x;
        if (x > Max) Max = x;
        AbsEnergy += x * x;
    }

    public void PushMany(ReadOnlySpan<float> xs)
    {
        for (int i = 0; i < xs.Length; i++) Push(xs[i]);
    }

    /// <summary>
    /// Sigma-shift between this distribution and another: how many
    /// standard deviations of the reference the means differ by,
    /// pooled with the RMS of the two std devs. Standard t-like metric.
    /// </summary>
    public double SigmaShift(DistributionStats reference)
    {
        double pooled = Math.Sqrt(0.5 * (Variance + reference.Variance));
        if (pooled == 0.0) return Math.Abs(Mean - reference.Mean) == 0 ? 0.0 : double.PositiveInfinity;
        return Math.Abs(Mean - reference.Mean) / pooled;
    }

    /// <summary>
    /// Cosine similarity between the two distributions' mean vectors
    /// projected to (mean, stddev). A tiny but useful "did anything
    /// move meaningfully" signal.
    /// </summary>
    public double CosineToward(DistributionStats other)
    {
        double dot = Mean * other.Mean + StdDev * other.StdDev;
        double a = Math.Sqrt(Mean * Mean + StdDev * StdDev);
        double b = Math.Sqrt(other.Mean * other.Mean + other.StdDev * other.StdDev);
        if (a == 0 || b == 0) return 1.0;
        return dot / (a * b);
    }
}

/// <summary>
/// Decodes the small numeric payload the Python worker attaches to
/// LOGITS / ACTIVATION events (top-k pairs, activation-summary shape+dtype)
/// into a floating-point sample list. Kept in this file because it's
/// tightly coupled to the payload layout in <c>hooks.py</c>.
/// </summary>
public static class EventPayload
{
    /// <summary>Extract logit values from an EVENT_LOGITS payload.</summary>
    public static IEnumerable<float> LogitsFrom(ReadOnlyMemory<byte> payload)
    {
        var s = payload.Span;
        if (s.Length < 4) yield break;
        int k = BinaryPrimitives.ReadInt32LittleEndian(s);
        int o = 4;
        for (int i = 0; i < k && o + 8 <= s.Length; i++, o += 8)
        {
            yield return BinaryPrimitives.ReadSingleLittleEndian(s.Slice(o + 4, 4));
        }
    }
}
