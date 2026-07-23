namespace StackScope.Core.Transactions;

/// <summary>
/// A single captured event. Domain-model twin of the protobuf
/// <c>Event</c>. Values of -1 mean "not applicable" for optional indices.
/// The payload is left opaque here (bytes) and interpreted by kind-specific
/// readers where needed.
/// </summary>
public sealed record TransactionEvent(
    ulong EventId,
    string TransactionId,
    long TimestampNs,
    EventKind Kind,
    int TokenIndex,
    int LayerIndex,
    int HeadIndex,
    int ThreadId,
    int StreamId,
    int DeviceId,
    ReadOnlyMemory<byte> Payload,
    IReadOnlyList<TraceMarker> Markers)
{
    public const int NotApplicable = -1;
}
