using StackScope.Core.Comparison;
using Xunit;

namespace StackScope.Core.Tests;

public class DistributionStatsTests
{
    [Fact]
    public void Mean_And_Std_Match_Reference_Implementation()
    {
        var xs = new float[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        var s = new DistributionStats();
        s.PushMany(xs);
        Assert.Equal(10, s.N);
        Assert.Equal(5.5, s.Mean, 6);
        Assert.Equal(1, s.Min);
        Assert.Equal(10, s.Max);
        // Sample stddev of 1..10 = sqrt(9.1666...) ≈ 3.02765
        Assert.InRange(s.StdDev, 3.0275, 3.0278);
    }

    [Fact]
    public void SigmaShift_Zero_For_Identical_Distributions()
    {
        var a = new DistributionStats();
        var b = new DistributionStats();
        for (int i = 0; i < 100; i++) { a.Push(i); b.Push(i); }
        Assert.Equal(0.0, a.SigmaShift(b), 9);
    }

    [Fact]
    public void SigmaShift_Detects_Mean_Drift()
    {
        var baseline = new DistributionStats();
        var shifted  = new DistributionStats();
        for (int i = 0; i < 100; i++) { baseline.Push(i); shifted.Push(i + 5); }
        // Same variance both sides, means differ by 5, stddev ≈ 29.01 → sigma ≈ 5/29 ≈ 0.17
        Assert.InRange(shifted.SigmaShift(baseline), 0.15, 0.20);
    }

    [Fact]
    public void CosineDistance_Zero_For_Colinear_Stats()
    {
        var a = new DistributionStats();
        var b = new DistributionStats();
        for (int i = 0; i < 50; i++) { a.Push(i); b.Push(i * 2); }
        // Both have (mean, std) vectors proportional-ish, cosine close to 1.
        Assert.InRange(1.0 - a.CosineToward(b), 0.0, 0.05);
    }
}
