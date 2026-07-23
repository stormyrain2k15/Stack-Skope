using StackScope.Core.Queries;
using StackScope.Core.Transactions;
using StackScope.Desktop.State;
using StackScope.Services;

namespace StackScope.Desktop.ViewModels;

/// <summary>ViewModel for the Tokens view. Filters events to TOKEN_BEGIN/TOKEN_END.</summary>
public sealed class TokensViewModel : EventViewModel
{
    public TokensViewModel(QueryService q) : base(q) {}
    protected override EventQuery BuildQuery() => new()
    {
        Kinds = new[] { EventKind.TokenBegin, EventKind.TokenEnd, EventKind.Logits }
    };
}

/// <summary>ViewModel for the Layers view. Layer boundaries + optional filter to selected token.</summary>
public sealed class LayersViewModel : EventViewModel
{
    public LayersViewModel(QueryService q) : base(q) {}
    protected override EventQuery BuildQuery()
    {
        var tok = SelectionState.Current.TokenIndex;
        return new()
        {
            Kinds = new[] { EventKind.LayerBegin, EventKind.LayerEnd },
            TokenIndex = tok >= 0 ? new IntRange(tok, tok) : IntRange.All
        };
    }
}

/// <summary>ViewModel for Attention view. QKV + Scores + Output; filter to selected (token, layer).</summary>
public sealed class AttentionViewModel : EventViewModel
{
    public AttentionViewModel(QueryService q) : base(q) {}
    protected override EventQuery BuildQuery()
    {
        var tok = SelectionState.Current.TokenIndex;
        var lay = SelectionState.Current.LayerIndex;
        return new()
        {
            Kinds = new[]
            {
                EventKind.AttentionQkv,
                EventKind.AttentionScores,
                EventKind.AttentionOutput
            },
            TokenIndex = tok >= 0 ? new IntRange(tok, tok) : IntRange.All,
            LayerIndex = lay >= 0 ? new IntRange(lay, lay) : IntRange.All
        };
    }
}

public sealed class ActivationsViewModel : EventViewModel
{
    public ActivationsViewModel(QueryService q) : base(q) {}
    protected override EventQuery BuildQuery()
    {
        var lay = SelectionState.Current.LayerIndex;
        return new()
        {
            Kinds = new[] { EventKind.Activation },
            LayerIndex = lay >= 0 ? new IntRange(lay, lay) : IntRange.All
        };
    }
}

public sealed class TensorsViewModel : EventViewModel
{
    public TensorsViewModel(QueryService q) : base(q) {}
    protected override EventQuery BuildQuery() => new()
    {
        Kinds = new[] { EventKind.TensorRead, EventKind.TensorWrite }
    };
}

public sealed class DriverViewModel : EventViewModel
{
    public DriverViewModel(QueryService q) : base(q) {}
    protected override EventQuery BuildQuery() => new()
    {
        Kinds = new[]
        {
            EventKind.KernelLaunch, EventKind.KernelEnd,
            EventKind.Memcpy, EventKind.Alloc, EventKind.Free
        }
    };
}

public sealed class KernelsViewModel : EventViewModel
{
    public KernelsViewModel(QueryService q) : base(q) {}
    protected override EventQuery BuildQuery() => new()
    {
        Kinds = new[] { EventKind.KernelLaunch, EventKind.KernelEnd }
    };
}

public sealed class MemoryViewModel : EventViewModel
{
    public MemoryViewModel(QueryService q) : base(q) {}
    protected override EventQuery BuildQuery() => new()
    {
        Kinds = new[] { EventKind.Alloc, EventKind.Free, EventKind.Memcpy }
    };
}

public sealed class TimelineViewModel : EventViewModel
{
    public TimelineViewModel(QueryService q) : base(q) {}
    protected override EventQuery BuildQuery()
    {
        // Timeline shows everything but reads sparsely — the view itself
        // renders trace-marker ranges from marker payloads.
        return new EventQuery { Limit = PageSize };
    }
}
