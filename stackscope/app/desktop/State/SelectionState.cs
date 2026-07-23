using CommunityToolkit.Mvvm.ComponentModel;
using StackScope.Core.Transactions;

namespace StackScope.Desktop.State;

/// <summary>
/// Global selection singleton. Every visual object is bound to a real
/// EventId (per project rule §38); selecting one in any view updates
/// this singleton, which every other view observes to keep the cross-
/// view "focus" in sync.
/// </summary>
public sealed partial class SelectionState : ObservableObject
{
    public static SelectionState Current { get; } = new();

    [ObservableProperty] private string? transactionId;
    [ObservableProperty] private ulong?  eventId;
    [ObservableProperty] private int     tokenIndex = -1;
    [ObservableProperty] private int     layerIndex = -1;
    [ObservableProperty] private int     headIndex  = -1;
    [ObservableProperty] private EventKind? kind;

    /// <summary>Selection history stack for navigation (Alt+Left / Alt+Right).</summary>
    private readonly List<SelectionSnapshot> _history = new();
    private int _historyIndex = -1;

    public void SelectEvent(TransactionEvent e)
    {
        Push(Snapshot());
        TransactionId = e.TransactionId;
        EventId = e.EventId;
        TokenIndex = e.TokenIndex;
        LayerIndex = e.LayerIndex;
        HeadIndex  = e.HeadIndex;
        Kind = e.Kind;
    }

    public void SelectToken(string txid, int tokenIndex)
    {
        Push(Snapshot());
        TransactionId = txid;
        TokenIndex = tokenIndex;
        LayerIndex = -1; HeadIndex = -1; EventId = null; Kind = null;
    }

    public void SelectLayer(string txid, int layer)
    {
        Push(Snapshot());
        TransactionId = txid;
        LayerIndex = layer;
        HeadIndex = -1; EventId = null; Kind = null;
    }

    public void SelectHead(string txid, int layer, int head)
    {
        Push(Snapshot());
        TransactionId = txid;
        LayerIndex = layer; HeadIndex = head;
        EventId = null; Kind = null;
    }

    public bool CanGoBack    => _historyIndex > 0;
    public bool CanGoForward => _historyIndex >= 0 && _historyIndex < _history.Count - 1;

    public void GoBack()
    {
        if (!CanGoBack) return;
        _historyIndex--;
        Apply(_history[_historyIndex]);
    }
    public void GoForward()
    {
        if (!CanGoForward) return;
        _historyIndex++;
        Apply(_history[_historyIndex]);
    }

    private SelectionSnapshot Snapshot()
        => new(TransactionId, EventId, TokenIndex, LayerIndex, HeadIndex, Kind);

    private void Push(SelectionSnapshot s)
    {
        if (_historyIndex >= 0 && _historyIndex < _history.Count - 1)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(s);
        _historyIndex = _history.Count - 1;
    }

    private void Apply(SelectionSnapshot s)
    {
        TransactionId = s.TransactionId;
        EventId = s.EventId;
        TokenIndex = s.TokenIndex;
        LayerIndex = s.LayerIndex;
        HeadIndex = s.HeadIndex;
        Kind = s.Kind;
    }
}

public sealed record SelectionSnapshot(
    string? TransactionId, ulong? EventId,
    int TokenIndex, int LayerIndex, int HeadIndex,
    EventKind? Kind);
