using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StackScope.Core.Comparison;
using StackScope.Core.Storage;
using StackScope.Services;

namespace StackScope.Desktop.ViewModels;

/// <summary>
/// One cell of the ablation-sweep heatmap. Each cell corresponds to a
/// single (layer, head) capture whose head was zeroed. <see cref="SigmaShift"/>
/// is the largest per-head sigma shift observed by
/// <see cref="HeadDiffAnalyzer"/> against the baseline; higher means the
/// zeroed head mattered more.
/// </summary>
public sealed partial class AblationSweepCell : ObservableObject
{
    [ObservableProperty] private int _layer;
    [ObservableProperty] private int _head;
    [ObservableProperty] private double _sigmaShift;
    [ObservableProperty] private string _transactionId = "";
    [ObservableProperty] private string _state = "queued";   // queued | running | done | failed
    [ObservableProperty] private string? _error;

    public string Tooltip =>
        $"L{Layer} · H{Head}\n" +
        (State == "queued"  ? "Waiting…" :
         State == "running" ? $"Running ({TransactionId})…" :
         State == "failed"  ? $"Failed: {Error}" :
                              $"σ={SigmaShift:F3}  txn {TransactionId}");

    /// <summary>
    /// Fill colour for the cell — deeper red for higher sigma. Uses a
    /// small palette that stays readable on both dark and light shell
    /// themes. NaN / not-yet-run cells render dim so users see the
    /// grid layout even before a sweep completes.
    /// </summary>
    public Brush Fill
    {
        get
        {
            if (State == "queued")  return new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x22));
            if (State == "running") return new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x2A));
            if (State == "failed")  return new SolidColorBrush(Color.FromRgb(0x6E, 0x22, 0x22));
            double s = Math.Clamp(SigmaShift / 5.0, 0.0, 1.0);
            byte r = (byte)(0x22 + s * (0xE0 - 0x22));
            byte g = (byte)(0x22 + (1.0 - s) * (0x88 - 0x22));
            byte b = 0x33;
            return new SolidColorBrush(Color.FromRgb(r, g, b));
        }
    }

    /// <summary>Nudge <see cref="Fill"/> re-evaluation. The sweep runner
    /// calls this after updating State + SigmaShift so bindings repaint
    /// even though Fill has no backing field observed by MVVM CT.</summary>
    public void RaiseFillChanged() => OnPropertyChanged(nameof(Fill));

    // Auto-refresh computed properties (Fill + Tooltip) whenever the
    // underlying observable fields change. Without these partials the
    // heatmap would stay grey until we manually poked it.
    partial void OnStateChanged(string value)
    {
        OnPropertyChanged(nameof(Fill));
        OnPropertyChanged(nameof(Tooltip));
    }
    partial void OnSigmaShiftChanged(double value)
    {
        OnPropertyChanged(nameof(Fill));
        OnPropertyChanged(nameof(Tooltip));
    }
    partial void OnTransactionIdChanged(string value) => OnPropertyChanged(nameof(Tooltip));
    partial void OnErrorChanged(string? value)        => OnPropertyChanged(nameof(Tooltip));
}

/// <summary>
/// Ablation sweep — for every cell in a rectangular (layer × head)
/// range, launches a single-cell ablation capture against the same
/// prompt as a chosen baseline, runs a diff, and lays out the
/// per-head contribution as a heatmap. This gives users a real
/// causal picture of which heads matter for the prompt, not just a
/// single-cell drop.
///
/// Uses the Coordinator gRPC surface directly rather than driving the
/// RunInferenceDialog, because the sweep needs to run N captures back
/// to back without user interaction and merge results into the heatmap.
/// </summary>
public sealed partial class AblationSweepViewModel : ObservableObject
{
    private readonly ProjectService _project;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _baselineTransactionId = "";
    [ObservableProperty] private int _layerStart;
    [ObservableProperty] private int _layerEnd;
    [ObservableProperty] private int _headStart;
    [ObservableProperty] private int _headEnd;
    [ObservableProperty] private double _sigmaThreshold = 1.0;
    [ObservableProperty] private string _status = "Pick a baseline, set a range, hit Run sweep.";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int _cellsDone;
    [ObservableProperty] private int _cellsTotal;

    public ObservableCollection<AblationSweepCell> Cells { get; } = new();

    public int LayerCount => Math.Max(1, LayerEnd - LayerStart + 1);
    public int HeadCount  => Math.Max(1, HeadEnd  - HeadStart + 1);

    public AblationSweepViewModel(ProjectService project) { _project = project; }

    partial void OnLayerStartChanged(int value) { OnPropertyChanged(nameof(LayerCount)); }
    partial void OnLayerEndChanged(int value)   { OnPropertyChanged(nameof(LayerCount)); }
    partial void OnHeadStartChanged(int value)  { OnPropertyChanged(nameof(HeadCount)); }
    partial void OnHeadEndChanged(int value)    { OnPropertyChanged(nameof(HeadCount)); }

    /// <summary>
    /// Kick off the sweep. Validates inputs, materialises the cell
    /// grid up front (so the UI shows the pending state immediately),
    /// then runs each ablated capture and updates its cell live.
    /// </summary>
    [RelayCommand]
    public async Task RunSweepAsync()
    {
        if (IsRunning) return;
        if (string.IsNullOrWhiteSpace(BaselineTransactionId))
        {
            Status = "Set a baseline transaction id first.";
            return;
        }
        var baseline = _project.ListTransactions()
            .FirstOrDefault(t => t.TransactionId == BaselineTransactionId);
        if (baseline is null)
        {
            Status = $"Baseline {BaselineTransactionId} not found.";
            return;
        }
        if (baseline.WasAblated)
        {
            Status = "Baseline must be a non-ablated capture.";
            return;
        }
        int L0 = Math.Min(LayerStart, LayerEnd);
        int L1 = Math.Max(LayerStart, LayerEnd);
        int H0 = Math.Min(HeadStart,  HeadEnd);
        int H1 = Math.Max(HeadStart,  HeadEnd);

        Cells.Clear();
        for (int l = L0; l <= L1; l++)
            for (int h = H0; h <= H1; h++)
                Cells.Add(new AblationSweepCell { Layer = l, Head = h, State = "queued" });
        CellsTotal = Cells.Count;
        CellsDone  = 0;
        IsRunning  = true;
        _cts = new CancellationTokenSource();
        Status = $"Running sweep: {LayerCount}×{HeadCount} = {CellsTotal} captures.";

        try
        {
            using var chan = Grpc.Net.Client.GrpcChannel.ForAddress(
                Environment.GetEnvironmentVariable("STACKSCOPE_COORDINATOR_ENDPOINT")
                    ?? "http://127.0.0.1:50600");
            var coord = new StackScope.Proto.V1.Coordinator.CoordinatorClient(chan);
            var workers = await coord.ListWorkersAsync(new StackScope.Proto.V1.ListWorkersRequest());
            if (workers.Workers.Count == 0)
            {
                Status = "No worker running. Load a model first.";
                IsRunning = false; return;
            }
            var workerId = workers.Workers[0].WorkerId;
            var handle   = StackScope.Desktop.State.WorkspaceState.Current.CurrentModelHandle;
            if (string.IsNullOrWhiteSpace(handle))
            {
                Status = "No model handle. Load a model first.";
                IsRunning = false; return;
            }

            foreach (var cell in Cells)
            {
                if (_cts.IsCancellationRequested) break;
                cell.State = "running";
                try
                {
                    var call = coord.RunInference(new StackScope.Proto.V1.CoordRunRequest
                    {
                        WorkerId = workerId,
                        ModelHandle = handle!,
                        Prompt = baseline.Prompt,
                        MaxNewTokens = 32,
                        Temperature = 0.0f,   // deterministic per cell so the diff is stable
                        TopP = 1.0f, TopK = 0,
                        Seed = 42,
                        CaptureLevel = StackScope.Proto.V1.CaptureLevel.CaptureAdvanced,
                        AblateLayer    = cell.Layer,
                        AblateHead     = cell.Head,
                        AblateLayerEnd = -1,
                        AblateHeadEnd  = -1,
                    }, cancellationToken: _cts.Token);

                    string txid = "";
                    await foreach (var prog in call.ResponseStream.ReadAllAsync(_cts.Token))
                    {
                        if (!string.IsNullOrEmpty(prog.TransactionId))
                            txid = prog.TransactionId;
                        if (prog.Finished)
                        {
                            if (!string.IsNullOrEmpty(prog.Error))
                                throw new InvalidOperationException(prog.Error);
                            break;
                        }
                    }
                    cell.TransactionId = txid;

                    // Diff the just-completed ablated capture against the
                    // baseline and take the peak sigma shift as the cell's
                    // heatmap value. Sync-run inside a Task to avoid
                    // blocking the UI thread on file I/O.
                    var peak = await Task.Run(() =>
                    {
                        using var A = new EventStore(baseline.TransactionId, _project.CapturesDir);
                        using var B = new EventStore(txid, _project.CapturesDir);
                        var rows = new HeadDiffAnalyzer(A, B).Rank(SigmaThreshold);
                        return rows.Count > 0 ? rows[0].SigmaShift : 0.0;
                    });
                    cell.SigmaShift = peak;
                    cell.State = "done";
                }
                catch (OperationCanceledException) { cell.State = "queued"; throw; }
                catch (Exception ex)
                {
                    cell.State = "failed";
                    cell.Error = ex.Message;
                }
                CellsDone++;
                Status = $"{CellsDone}/{CellsTotal} — L{cell.Layer} H{cell.Head} → σ={cell.SigmaShift:F3}";
                cell.RaiseFillChanged();
            }
            Status = _cts.IsCancellationRequested
                ? $"Sweep cancelled after {CellsDone}/{CellsTotal} cells."
                : $"Sweep complete: {CellsDone}/{CellsTotal} cells. Top σ = {Cells.Max(c => c.SigmaShift):F3}.";
        }
        catch (OperationCanceledException) { Status = "Sweep cancelled."; }
        catch (Exception ex) { Status = "Sweep failed: " + ex.Message; }
        finally { IsRunning = false; _cts?.Dispose(); _cts = null; }
    }

    [RelayCommand]
    public void CancelSweep()
    {
        if (!IsRunning) { Status = "Not running."; return; }
        _cts?.Cancel();
        Status = "Cancel requested…";
    }
}
