using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StackScope.Core.Analysis;
using StackScope.Core.Storage;
using StackScope.Desktop.State;
using StackScope.Services;

namespace StackScope.Desktop.ViewModels;

public sealed partial class DivergenceViewModel : ObservableObject
{
    private readonly ProjectService _project;
    public ObservableCollection<string> Runs { get; } = new();
    [ObservableProperty] private double sigmaThreshold = 1.0;
    [ObservableProperty] private string result = "";
    [ObservableProperty] private string runsInput = "";

    public DivergenceViewModel(ProjectService project) { _project = project; }

    [RelayCommand]
    public void Analyze()
    {
        var ids = RunsInput.Split(new[] { ',', ' ', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (ids.Count < 2) { Result = "Provide at least two transaction ids (comma-separated)."; return; }

        var stores = new List<EventStore>();
        try
        {
            foreach (var id in ids) stores.Add(new EventStore(id, _project.CapturesDir));
            var det = new DivergenceDetector(stores);
            var r = det.Detect(SigmaThreshold);
            Result = r.FirstDivergentToken < 0
                ? "Runs identical up to the shortest length."
                : $"First divergence at token {r.FirstDivergentToken}, earliest layer L{r.FirstDivergentLayer} "
                  + $"(peak σ = {r.MaxSigmaAtDivergence:F2}). Sampled ids: [{string.Join(", ", r.SampledIdsPerRun)}]";
        }
        catch (Exception ex) { Result = $"Failed: {ex.Message}"; }
        finally { foreach (var s in stores) s.Dispose(); }
    }
}

public sealed partial class CircuitTraceViewModel : ObservableObject
{
    private readonly ProjectService _project;
    public ObservableCollection<CircuitTracer.Node> Path { get; } = new();
    [ObservableProperty] private int tokenIndex;
    [ObservableProperty] private string status = "";

    public CircuitTraceViewModel(ProjectService project)
    {
        _project = project;
        SelectionState.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SelectionState.Current.TokenIndex))
                TokenIndex = Math.Max(0, SelectionState.Current.TokenIndex);
        };
    }

    [RelayCommand]
    public void Trace()
    {
        Path.Clear();
        var txid = SelectionState.Current.TransactionId
                   ?? WorkspaceState.Current.CurrentTransactionId;
        if (txid is null) { Status = "No transaction selected."; return; }
        using var store = new EventStore(txid, _project.CapturesDir);
        var tracer = new CircuitTracer(store);
        var p = tracer.TraceFromToken(TokenIndex);
        foreach (var n in p.Nodes) Path.Add(n);
        Status = $"{Path.Count} nodes on the trace for token {TokenIndex}.";
    }
}

public sealed partial class AblationViewModel : ObservableObject
{
    [ObservableProperty] private int ablateLayer = -1;
    [ObservableProperty] private int ablateHead  = -1;
    /// <summary>
    /// Inclusive end of the rectangular range to zero in one capture.
    /// -1 means "single cell" (matches AblateLayer). ≥ AblateLayer
    /// activates the range so every head in
    /// [AblateLayer..AblateLayerEnd] × [AblateHead..AblateHeadEnd] is
    /// zeroed together — one capture, multi-cell ablation.
    /// </summary>
    [ObservableProperty] private int ablateLayerEnd = -1;
    [ObservableProperty] private int ablateHeadEnd  = -1;

    /// <summary>
    /// Side ordering for the auto-diff opened after an ablated capture.
    /// false (default) = Left: baseline, Right: ablated — the standard
    /// "control vs treatment" convention read left-to-right.
    /// true = Left: ablated, Right: baseline — useful when the eye
    /// should land on the run you just triggered.
    /// </summary>
    [ObservableProperty] private bool autoCompareAblatedOnLeft;

    /// <summary>
    /// Sigma threshold seeded into <c>CompareDiffViewModel</c> when the
    /// auto-diff kicks off. Ablation shifts are often subtle, so 1.0σ
    /// is a friendlier default than the 1.5σ CompareView uses for
    /// unrelated A/B analysis. User can override before pressing F5.
    /// </summary>
    [ObservableProperty] private double autoCompareSigma = 1.0;

    [ObservableProperty] private string status =
        "Set layer/head ≥ 0, then press F5 (Start Capture). Set the *_end fields "
        + "≥ their starts to zero a rectangular range in one capture, or leave -1 for a single cell. "
        + "When the ablated capture finishes, StackScope automatically opens Diff Mode against the "
        + "newest non-ablated run of the same prompt so you see the head's contribution in one click. "
        + "For a per-cell contribution heatmap, use the Sweep view. -1/-1 means no ablation.";
}
