using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StackScope.Core.Comparison;
using StackScope.Core.Storage;
using StackScope.Services;

namespace StackScope.Desktop.ViewModels;

/// <summary>
/// ViewModel for the Compare view + Diff Mode.
///
/// Diff Mode input: two transaction IDs (baseline vs candidate).
/// Output: a ranked list of (layer, head) cells whose distribution has
/// drifted, sortable and threshold-filterable. This is the concrete UI
/// binding for <see cref="HeadDiffAnalyzer"/>.
/// </summary>
public sealed partial class CompareDiffViewModel : ObservableObject
{
    private readonly ProjectService _project;

    [ObservableProperty] private string? leftTransactionId;
    [ObservableProperty] private string? rightTransactionId;
    [ObservableProperty] private double  sigmaThreshold = 1.5;
    [ObservableProperty] private bool    isBusy;
    [ObservableProperty] private string  statusMessage = "";
    [ObservableProperty] private long    onlyInLeftCount;
    [ObservableProperty] private long    onlyInRightCount;
    [ObservableProperty] private long    inBothCount;

    public System.Collections.ObjectModel.ObservableCollection<HeadDiffAnalyzer.Row> Ranking { get; }
        = new();

    public CompareDiffViewModel(ProjectService project) { _project = project; }

    [RelayCommand]
    public async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(LeftTransactionId) ||
            string.IsNullOrWhiteSpace(RightTransactionId))
        {
            StatusMessage = "Pick a left and right transaction id.";
            return;
        }

        IsBusy = true; Ranking.Clear();
        StatusMessage = "Analyzing…";
        try
        {
            var rows = await Task.Run(() =>
            {
                using var A = new EventStore(LeftTransactionId!,  _project.CapturesDir);
                using var B = new EventStore(RightTransactionId!, _project.CapturesDir);
                var analyzer = new HeadDiffAnalyzer(A, B);
                return analyzer.Rank(SigmaThreshold);
            });

            foreach (var r in rows) Ranking.Add(r);
            StatusMessage = rows.Count == 0
                ? $"No cells drifted ≥ {SigmaThreshold:F2}σ. Baseline and candidate are indistinguishable at this threshold."
                : $"{rows.Count} cells drifted ≥ {SigmaThreshold:F2}σ. Top: L{rows[0].Layer}·H{rows[0].Head} at {rows[0].SigmaShift:F2}σ.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Diff failed: {ex.Message}";
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public void ClearRanking()
    {
        Ranking.Clear();
        StatusMessage = "";
    }
}
