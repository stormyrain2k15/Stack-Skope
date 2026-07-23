using StackScope.Core.Transactions;

namespace StackScope.Core.Correlation;

/// <summary>
/// A pair of correlated events + the confidence with which they were
/// matched. Always includes both endpoints so the UI can navigate
/// bidirectionally.
/// </summary>
public sealed record CorrelatedPair(
    TransactionEvent Left,
    TransactionEvent Right,
    CorrelationConfidence Confidence,
    string Reason);
