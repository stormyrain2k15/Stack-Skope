using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StackScope.Desktop.State;
using StackScope.Services;

namespace StackScope.Desktop.ViewModels;

/// <summary>Capture Library view — lists all captured transactions in the project.</summary>
public sealed partial class CaptureLibraryViewModel : ObservableObject
{
    private readonly ProjectService _project;

    public System.Collections.ObjectModel.ObservableCollection<TransactionMetadata> Transactions { get; }
        = new();

    [ObservableProperty] private TransactionMetadata? selectedTransaction;

    public CaptureLibraryViewModel(ProjectService project)
    {
        _project = project;
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        Transactions.Clear();
        foreach (var t in _project.ListTransactions()) Transactions.Add(t);
    }

    partial void OnSelectedTransactionChanged(TransactionMetadata? value)
    {
        if (value is null) return;
        WorkspaceState.Current.CurrentTransactionId = value.TransactionId;
        SelectionState.Current.TransactionId = value.TransactionId;
        // Keep the capture-ceiling badge in sync with the currently
        // selected transaction — otherwise the badge from an earlier
        // capture would stick around when the user switches library rows.
        WorkspaceState.Current.CaptureCeiling = value.CaptureCeiling;
    }
}

/// <summary>Overview: top-level model + transaction summary.</summary>
public sealed partial class OverviewViewModel : ObservableObject
{
    [ObservableProperty] private string? modelName;
    [ObservableProperty] private string? architecture;
    [ObservableProperty] private int nLayers;
    [ObservableProperty] private int nHeads;
    [ObservableProperty] private int hiddenSize;
    [ObservableProperty] private int vocabSize;
    [ObservableProperty] private long transactionEventCount;
    [ObservableProperty] private int transactionTokenCount;
    [ObservableProperty] private string? transactionId;
    [ObservableProperty] private bool transactionCompleted;

    public OverviewViewModel()
    {
        SelectionState.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SelectionState.Current.TransactionId))
                TransactionId = SelectionState.Current.TransactionId;
        };
    }

    /// <summary>
    /// Pull real counts + completion status for the current transaction
    /// from the project + query services. Called after a capture
    /// finishes and whenever the user opens a project that already has
    /// transactions.
    /// </summary>
    public void RefreshTransactionStats(StackScope.Services.ProjectService project,
                                        StackScope.Services.QueryService query)
    {
        if (string.IsNullOrWhiteSpace(TransactionId)) return;
        var meta = project.ListTransactions()
            .FirstOrDefault(t => t.TransactionId == TransactionId);
        if (meta is not null)
        {
            TransactionCompleted = meta.Completed;
            if (string.IsNullOrEmpty(Architecture)) Architecture = meta.Architecture;
        }
        TransactionEventCount = query.Count(TransactionId!, new StackScope.Core.Queries.EventQuery());
        TransactionTokenCount = (int)query.Count(TransactionId!, new StackScope.Core.Queries.EventQuery
        {
            Kinds = new[] { StackScope.Core.Transactions.EventKind.TokenEnd },
        });
    }
}

/// <summary>Compare view — pairs two transactions for A/B analysis.</summary>
public sealed partial class CompareViewModel : ObservableObject
{
    [ObservableProperty] private string? leftTransactionId;
    [ObservableProperty] private string? rightTransactionId;
    [ObservableProperty] private long onlyInLeftCount;
    [ObservableProperty] private long onlyInRightCount;
    [ObservableProperty] private long inBothCount;
}
