namespace StackScope.Core.Correlation;

/// <summary>
/// Confidence with which two events were correlated. Never rendered as
/// unlabelled certainty in the UI — every correlation carries its
/// confidence next to it (per project rule §38).
/// </summary>
public enum CorrelationConfidence
{
    /// <summary>Direct: same worker, same thread, same explicit ID.</summary>
    Direct = 0,
    /// <summary>Coupled via the runtime's own correlation ID (CUPTI/NVTX).</summary>
    RuntimeCorrelated = 1,
    /// <summary>Matched by (stream, address, size) of a memory transaction.</summary>
    AddressCorrelated = 2,
    /// <summary>Contained inside a semantic trace marker range.</summary>
    MarkerCorrelated = 3,
    /// <summary>Nearest-in-time on the same thread/stream.</summary>
    TimeCorrelated = 4,
    /// <summary>Inferred by heuristic; label as such and treat as advisory only.</summary>
    Inferred = 5
}
