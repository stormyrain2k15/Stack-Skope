namespace StackScope.Core.Transactions;

/// <summary>
/// A single inference transaction — one prompt run end-to-end. All events
/// captured for the run share the same <see cref="TransactionId"/>.
/// </summary>
public sealed class InferenceTransaction
{
    public required string TransactionId { get; init; }   // ULID
    public required string ModelHandle { get; init; }
    public required string Architecture { get; init; }
    public required long StartedNs { get; init; }
    public long EndedNs { get; set; }
    public ulong EventCount { get; set; }
    public int TokenCount { get; set; }
    public bool Completed { get; set; }
    public string? Error { get; set; }

    /// <summary>Duration in nanoseconds, or -1 if still running.</summary>
    public long DurationNs => Completed && EndedNs > 0 ? EndedNs - StartedNs : -1;
}
