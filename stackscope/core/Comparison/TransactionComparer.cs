using StackScope.Core.Queries;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;

namespace StackScope.Core.Comparison;

/// <summary>
/// Compares two inference transactions and yields a stream of per-event
/// diffs keyed on (kind, token, layer, head). Useful for A/B'ing two
/// quantization schemes or two random seeds.
/// </summary>
public sealed class TransactionComparer
{
    private readonly QueryEngine _a;
    private readonly QueryEngine _b;

    public TransactionComparer(EventStore a, EventStore b)
    {
        _a = new QueryEngine(a);
        _b = new QueryEngine(b);
    }

    public IEnumerable<Diff> Compare(EventQuery q)
    {
        var left  = _a.Query(q).ToDictionary(KeyOf);
        var right = _b.Query(q).ToDictionary(KeyOf);

        var keys = new HashSet<(EventKind, int, int, int)>(left.Keys);
        keys.UnionWith(right.Keys);

        foreach (var k in keys.OrderBy(x => x.Item2)   // token
                              .ThenBy(x => x.Item3)   // layer
                              .ThenBy(x => x.Item4))  // head
        {
            left.TryGetValue(k, out var l);
            right.TryGetValue(k, out var r);
            yield return new Diff(k.Item1, k.Item2, k.Item3, k.Item4, l, r);
        }
    }

    private static (EventKind, int, int, int) KeyOf(TransactionEvent e)
        => (e.Kind, e.TokenIndex, e.LayerIndex, e.HeadIndex);

    public sealed record Diff(
        EventKind Kind,
        int Token,
        int Layer,
        int Head,
        TransactionEvent? Left,
        TransactionEvent? Right)
    {
        public bool OnlyInLeft  => Right is null && Left  is not null;
        public bool OnlyInRight => Left  is null && Right is not null;
        public bool Both        => Left is not null && Right is not null;
    }
}
