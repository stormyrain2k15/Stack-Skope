namespace StackScope.Core.Models;

/// <summary>
/// Best-effort memory estimate for loading the model, per device kind.
/// </summary>
public sealed record MemoryEstimate(
    long WeightsBytes,
    long KvCachePerTokenBytes,
    long ActivationsPerTokenBytes,
    long OverheadBytes)
{
    /// <summary>Total bytes needed for a sequence of length <paramref name="ctxLen"/>.</summary>
    public long TotalForContext(int ctxLen)
        => WeightsBytes
         + KvCachePerTokenBytes * ctxLen
         + ActivationsPerTokenBytes * ctxLen
         + OverheadBytes;
}
