namespace StackScope.Core.Transactions;

/// <summary>
/// A marker injected around a semantic region during inference. Used to
/// correlate driver-level events (kernel launches, memcpys) with the
/// worker-level semantic boundaries they belong to.
/// </summary>
public sealed record TraceMarker(
    string Name,
    long BeginNs,
    long EndNs,
    uint ColorRgba,
    int ThreadId,
    int StreamId,
    ulong CorrelationId)
{
    public bool IsPoint => EndNs == 0;
    public long DurationNs => IsPoint ? 0 : EndNs - BeginNs;
    public bool Contains(long tsNs) => tsNs >= BeginNs && (IsPoint || tsNs <= EndNs);
}
