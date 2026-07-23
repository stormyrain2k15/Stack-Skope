using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StackScope.Core.Comparison;
using StackScope.Core.Models;
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
    [ObservableProperty] private string _prompt = "";
    [ObservableProperty] private int _promptIndex;
    [ObservableProperty] private double _sigmaShift;
    [ObservableProperty] private string _transactionId = "";
    [ObservableProperty] private string _state = "queued";   // queued | running | done | failed
    [ObservableProperty] private string? _error;

    public string Tooltip =>
        $"prompt #{PromptIndex}: {Prompt}\nL{Layer} · H{Head}\n" +
        (State == "queued"  ? "Waiting…" :
         State == "running" ? $"Running ({TransactionId})…" :
         State == "failed"  ? $"Failed: {Error}" :
                              $"σ={SigmaShift:F3}  txn {TransactionId}\n(click to pin against baseline)");

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
    private readonly PinnedDiffsViewModel _pinBoardVm;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _baselineTransactionId = "";
    [ObservableProperty] private int _layerStart;
    [ObservableProperty] private int _layerEnd;
    [ObservableProperty] private int _headStart;
    [ObservableProperty] private int _headEnd;
    [ObservableProperty] private double _sigmaThreshold = 1.0;
    /// <summary>Extra prompts (one per line) for cross-prompt attribution.
    /// The baseline transaction's own prompt is always the first column;
    /// each additional non-empty line runs the same sweep against that
    /// prompt using the baseline's model handle.</summary>
    [ObservableProperty] private string _extraPromptsText = "";
    [ObservableProperty] private bool _resumeFromLastRun = true;
    [ObservableProperty] private string _status = "Pick a baseline, set a range, hit Run sweep.";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int _cellsDone;
    [ObservableProperty] private int _cellsTotal;

    public ObservableCollection<AblationSweepCell> Cells { get; } = new();

    public int LayerCount  => Math.Max(1, LayerEnd - LayerStart + 1);
    public int HeadCount   => Math.Max(1, HeadEnd  - HeadStart + 1);
    public int PromptCount { get; private set; } = 1;

    public AblationSweepViewModel(ProjectService project, PinnedDiffsViewModel pinBoardVm)
    {
        _project = project;
        _pinBoardVm = pinBoardVm;
    }

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

        // Build prompt column list. Column 0 is always the baseline
        // prompt (needed for the baseline diff to be meaningful);
        // extras follow in the order the user typed them, blank lines
        // and duplicates dropped so the heatmap columns are unique.
        var prompts = new List<string> { baseline.Prompt ?? "" };
        foreach (var line in (ExtraPromptsText ?? "")
                             .Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.Trim();
            if (p.Length > 0 && !prompts.Contains(p)) prompts.Add(p);
        }
        PromptCount = prompts.Count;
        OnPropertyChanged(nameof(PromptCount));

        // Resume support: load previous cells from disk keyed by
        // (baseline, range, prompt) so a cancelled/crashed sweep can
        // pick up where it stopped instead of restarting at L0/H0.
        var resumeMap = ResumeFromLastRun
            ? LoadResumeState(BaselineTransactionId, L0, L1, H0, H1, prompts)
            : new Dictionary<string, (string txid, double sigma)>(StringComparer.Ordinal);

        Cells.Clear();
        for (int pi = 0; pi < prompts.Count; pi++)
        {
            for (int l = L0; l <= L1; l++)
                for (int h = H0; h <= H1; h++)
                {
                    var cell = new AblationSweepCell
                    {
                        Layer = l, Head = h,
                        Prompt = prompts[pi], PromptIndex = pi,
                        State = "queued",
                    };
                    var key = ResumeKey(pi, l, h);
                    if (resumeMap.TryGetValue(key, out var prior))
                    {
                        cell.TransactionId = prior.txid;
                        cell.SigmaShift    = prior.sigma;
                        cell.State         = "done";
                    }
                    Cells.Add(cell);
                }
        }
        CellsTotal = Cells.Count;
        CellsDone  = Cells.Count(c => c.State == "done");
        IsRunning  = true;
        _cts = new CancellationTokenSource();
        Status = resumeMap.Count > 0
            ? $"Resuming {CellsDone}/{CellsTotal} pre-computed cells."
            : $"Running sweep: {prompts.Count}×{LayerCount}×{HeadCount} = {CellsTotal} captures.";

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

            // Speedup: pre-resolve the per-prompt baseline metadata *once*
            // and open the baseline EventStore *once per prompt column*
            // instead of doing a full ListTransactions() disk scan and
            // reopening the store on every cell. Column 0 uses the
            // sweep's own baseline; extras must exist as non-ablated
            // captures of that prompt (thrown early if missing).
            var perPromptBaseline = new Dictionary<string, TransactionMetadata>(StringComparer.Ordinal);
            var perPromptStores   = new Dictionary<string, EventStore>(StringComparer.Ordinal);
            perPromptBaseline[baseline.Prompt ?? ""] = baseline;
            perPromptStores[baseline.Prompt ?? ""]   =
                new EventStore(baseline.TransactionId, _project.CapturesDir);
            for (int pi = 1; pi < prompts.Count; pi++)
            {
                var pmatch = FindOrThrowBaselineForPrompt(prompts[pi]);
                perPromptBaseline[prompts[pi]] = pmatch;
                perPromptStores[prompts[pi]]   =
                    new EventStore(pmatch.TransactionId, _project.CapturesDir);
            }

            foreach (var cell in Cells)
            {
                if (_cts.IsCancellationRequested) break;
                if (cell.State == "done") continue;   // resumed from disk
                cell.State = "running";
                try
                {
                    var call = coord.RunInference(new StackScope.Proto.V1.CoordRunRequest
                    {
                        WorkerId = workerId,
                        ModelHandle = handle!,
                        Prompt = cell.Prompt,
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

                    // Reuse the pre-opened baseline EventStore for this
                    // prompt column instead of re-opening on every cell.
                    // The ablated store is per-cell so it stays scoped.
                    var A = perPromptStores[cell.Prompt];
                    var peak = await Task.Run(() =>
                    {
                        using var B = new EventStore(txid, _project.CapturesDir);
                        var rows = new HeadDiffAnalyzer(A, B).Rank(SigmaThreshold);
                        return rows.Count > 0 ? rows[0].SigmaShift : 0.0;
                    });
                    cell.SigmaShift = peak;
                    cell.State = "done";
                    PersistCellResult(BaselineTransactionId, L0, L1, H0, H1, prompts, cell);
                }
                catch (OperationCanceledException) { cell.State = "queued"; throw; }
                catch (Exception ex)
                {
                    cell.State = "failed";
                    cell.Error = ex.Message;
                }
                CellsDone++;
                Status = $"{CellsDone}/{CellsTotal} — p{cell.PromptIndex} L{cell.Layer} H{cell.Head} → σ={cell.SigmaShift:F3}";
                cell.RaiseFillChanged();
            }
            Status = _cts.IsCancellationRequested
                ? $"Sweep cancelled after {CellsDone}/{CellsTotal} cells."
                : $"Sweep complete: {CellsDone}/{CellsTotal} cells. Top σ = {Cells.Max(c => c.SigmaShift):F3}.";

            // Release the per-prompt baseline EventStore handles now
            // that no more diffs will run against them.
            foreach (var s in perPromptStores.Values) s.Dispose();
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

    /// <summary>
    /// Auto-pin the (baseline ⇆ this-cell's ablated txn) pair into
    /// the persistent Pin Board. Called from the sweep heatmap when
    /// the user clicks a completed cell. Refuses to pin cells that
    /// aren't done (nothing meaningful to compare yet).
    /// </summary>
    [RelayCommand]
    public void PinCell(AblationSweepCell? cell)
    {
        if (cell is null) { Status = "No cell to pin."; return; }
        if (cell.State != "done" || string.IsNullOrEmpty(cell.TransactionId))
        {
            Status = $"Cell L{cell.Layer} H{cell.Head} is not done — nothing to pin.";
            return;
        }
        var baseline = cell.PromptIndex == 0
            ? BaselineTransactionId
            : TryFindBaselineForPrompt(cell.Prompt)?.TransactionId ?? BaselineTransactionId;
        try
        {
            using var store = new PinnedDiffStore(_project.PinnedDiffsDbPath);
            store.Add(new PinnedDiff(
                Id: 0,
                LeftTransactionId:  baseline,
                RightTransactionId: cell.TransactionId,
                SigmaThreshold: SigmaThreshold,
                CreatedAtNs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L,
                Note: $"sweep p{cell.PromptIndex} L{cell.Layer} H{cell.Head} σ={cell.SigmaShift:F3}",
                Tags: "sweep,ablation"));
        }
        catch (Exception ex) { Status = "Pin failed: " + ex.Message; return; }
        _pinBoardVm.Refresh();
        Status = $"Pinned L{cell.Layer} H{cell.Head} (σ={cell.SigmaShift:F3}) → Pin Board.";
    }

    // ---- Cross-prompt baseline resolution ------------------------------
    // For column 0 the sweep's own BaselineTransactionId is the diff
    // reference. For columns >0 we need a non-ablated capture of that
    // extra prompt. We look one up in the project so cross-prompt
    // attribution numbers stay honest — refusing to synthesise a diff
    // when no baseline exists.

    private TransactionMetadata? TryFindBaselineForPrompt(string prompt)
        => _project.ListTransactions().FirstOrDefault(t =>
            t.Completed && !t.WasAblated &&
            string.Equals(t.Prompt, prompt, StringComparison.Ordinal));

    private TransactionMetadata FindOrThrowBaselineForPrompt(string prompt)
        => TryFindBaselineForPrompt(prompt)
           ?? throw new InvalidOperationException(
               $"Cross-prompt column needs a non-ablated capture of prompt \"{prompt}\" — none found. "
             + "Run that prompt once without ablation before sweeping.");

    // ---- Resume state persistence -------------------------------------
    // Keyed by (baseline txn id + range + prompt index). Stored as a
    // JSON sidecar next to the project's pinned_diffs.sqlite so it
    // survives crashes and app restarts. Deliberately not in the same
    // sqlite: sweep results are transient by nature, we don't want them
    // clogging the durable pin store.

    private string ResumeFilePath(string baseline, int L0, int L1, int H0, int H1)
        => Path.Combine(_project.RootDir,
            $"sweep-resume-{baseline}-L{L0}_{L1}-H{H0}_{H1}.json");

    private static string ResumeKey(int promptIndex, int layer, int head)
        => $"{promptIndex}:{layer}:{head}";

    private Dictionary<string, (string txid, double sigma)> LoadResumeState(
        string baseline, int L0, int L1, int H0, int H1, IReadOnlyList<string> prompts)
    {
        var map = new Dictionary<string, (string, double)>(StringComparer.Ordinal);
        var path = ResumeFilePath(baseline, L0, L1, H0, H1);
        if (!File.Exists(path)) return map;
        try
        {
            var raw = File.ReadAllText(path);
            var rows = JsonSerializer.Deserialize<List<ResumeRow>>(raw) ?? new();
            var promptIdx = prompts
                .Select((p, i) => (p, i))
                .ToDictionary(x => x.p, x => x.i, StringComparer.Ordinal);
            foreach (var r in rows)
            {
                if (!promptIdx.TryGetValue(r.Prompt ?? "", out var pi)) continue;
                map[ResumeKey(pi, r.Layer, r.Head)] = (r.TxId ?? "", r.Sigma);
            }
        }
        catch { /* corrupt file → start fresh */ }
        return map;
    }

    private void PersistCellResult(string baseline, int L0, int L1, int H0, int H1,
                                    IReadOnlyList<string> prompts, AblationSweepCell cell)
    {
        var path = ResumeFilePath(baseline, L0, L1, H0, H1);
        var rows = new List<ResumeRow>();
        try
        {
            if (File.Exists(path))
                rows = JsonSerializer.Deserialize<List<ResumeRow>>(File.ReadAllText(path)) ?? new();
        }
        catch { rows = new(); }
        // Upsert this cell — key on (prompt, layer, head).
        rows.RemoveAll(r =>
            string.Equals(r.Prompt, cell.Prompt, StringComparison.Ordinal) &&
            r.Layer == cell.Layer && r.Head == cell.Head);
        rows.Add(new ResumeRow
        {
            Prompt = cell.Prompt, Layer = cell.Layer, Head = cell.Head,
            TxId = cell.TransactionId, Sigma = cell.SigmaShift,
        });
        try { File.WriteAllText(path, JsonSerializer.Serialize(rows)); }
        catch { /* best-effort resume; never let a write failure kill the sweep */ }
    }

    private sealed class ResumeRow
    {
        public string? Prompt { get; set; }
        public int    Layer  { get; set; }
        public int    Head   { get; set; }
        public string? TxId  { get; set; }
        public double Sigma  { get; set; }
    }
}
