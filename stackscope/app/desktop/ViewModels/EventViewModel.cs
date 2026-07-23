using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StackScope.Core.Queries;
using StackScope.Core.Transactions;
using StackScope.Desktop.State;
using StackScope.Services;

namespace StackScope.Desktop.ViewModels;

/// <summary>
/// Base class for a view that queries the event store. Handles paging,
/// filter binding to WorkspaceState, and hot-reload when the selection
/// changes across views.
/// </summary>
public abstract partial class EventViewModel : ObservableObject
{
    protected readonly QueryService QueryService;

    [ObservableProperty] private bool isLoading;
    [ObservableProperty] private long totalCount;
    [ObservableProperty] private int  pageSize = 500;
    [ObservableProperty] private int  page = 0;

    public System.Collections.ObjectModel.ObservableCollection<TransactionEvent> Events { get; }
        = new();

    protected EventViewModel(QueryService queryService)
    {
        QueryService = queryService;
        SelectionState.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SelectionState.Current.TransactionId)
                or nameof(SelectionState.Current.TokenIndex)
                or nameof(SelectionState.Current.LayerIndex)
                or nameof(SelectionState.Current.HeadIndex))
            {
                _ = RefreshAsync();
            }
        };
    }

    protected abstract EventQuery BuildQuery();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var txid = SelectionState.Current.TransactionId
                   ?? WorkspaceState.Current.CurrentTransactionId;
        if (txid is null)
        {
            Events.Clear();
            TotalCount = 0;
            return;
        }
        IsLoading = true;
        try
        {
            var q = BuildQuery() with
            {
                Offset = (long)Page * PageSize,
                Limit = PageSize
            };
            var results = await Task.Run(() => QueryService.Query(txid, q).ToList());
            TotalCount = await Task.Run(() => QueryService.Count(txid, q));

            Events.Clear();
            foreach (var e in results) Events.Add(e);
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    public void Select(TransactionEvent e) => SelectionState.Current.SelectEvent(e);
}
