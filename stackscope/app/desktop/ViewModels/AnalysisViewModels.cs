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
    [ObservableProperty] private string status =
        "Set layer/head ≥ 0, then press F5 (Start Capture). The values here seed the "
        + "capture dialog and are forwarded through RunInference so the worker zeroes that head "
        + "before returning. When the ablated capture finishes, StackScope automatically opens "
        + "Diff Mode against the newest non-ablated run of the same prompt so you see the head's "
        + "contribution in one click. -1/-1 means no ablation.";
}
