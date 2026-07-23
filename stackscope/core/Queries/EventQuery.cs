using StackScope.Core.Transactions;

namespace StackScope.Core.Queries;

/// <summary>
/// Range filter on a single integer dimension. -1 means "unbounded".
/// </summary>
public readonly record struct IntRange(int From, int To)
{
    public static IntRange All => new(-1, -1);
    public bool Contains(int value)
        => (From == -1 || value >= From) && (To == -1 || value <= To);

    public bool IsUnbounded => From == -1 && To == -1;
}

/// <summary>
/// A query against the event store. Any combination of filters may be set;
/// unset (default) filters are treated as unbounded.
/// </summary>
public sealed record EventQuery
{
    public IReadOnlyList<EventKind> Kinds { get; init; } = Array.Empty<EventKind>();
    public IntRange TokenIndex { get; init; } = IntRange.All;
    public IntRange LayerIndex { get; init; } = IntRange.All;
    public IntRange HeadIndex  { get; init; } = IntRange.All;
    public long TimeFromNs { get; init; } = 0;
    public long TimeToNs   { get; init; } = long.MaxValue;
    public long Offset { get; init; } = 0;   // for virtualised paging
    public int Limit { get; init; } = 512;
}
