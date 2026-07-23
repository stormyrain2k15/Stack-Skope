using System.IO;
using System.Windows;
using System.Windows.Input;
using AvalonDock.Layout.Serialization;
using Microsoft.Win32;
using StackScope.Adapters.Architectures;
using StackScope.Desktop.State;
using StackScope.Desktop.ViewModels;
using StackScope.Services;

namespace StackScope.Desktop;

public partial class MainWindow : Window
{
    private ProjectService _project;
    private QueryService _query;
    private ShellViewModel _shell;

    public MainWindow()
    {
        InitializeComponent();

        var projectRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StackScope", "default");
        _project = new ProjectService(projectRoot);
        _query   = new QueryService(_project);
        WorkspaceState.Current.ProjectRoot = projectRoot;

        _shell = new ShellViewModel(_project, _query);
        DataContext = _shell;

        SelectionState.Current.PropertyChanged += (_, __) => _shell.RefreshInspectorFromSelection();
        WorkspaceState.Current.PropertyChanged += (_, __) => _shell.NotifyWorkspaceChanged();
        _shell.RefreshInspectorFromSelection();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TryLoadSavedLayout();
        CheckForRecoverableCaptures();
    }

    private void TryLoadSavedLayout()
    {
        var path = Path.Combine(_project.LayoutsDir, "current.xml");
        if (!File.Exists(path)) return;
        try
        {
            using var reader = new StreamReader(path);
            new XmlLayoutSerializer(Dock).Deserialize(reader);
        }
        catch (Exception ex) { StatusText.Text = $"Layout load failed: {ex.Message}"; }
    }

    private void CheckForRecoverableCaptures()
    {
        var partial = _project.ListTransactions().Where(t => !t.Completed).ToList();
        if (partial.Count == 0) return;
        var newest = partial[0];
        WorkspaceState.Current.RecoveryBanner =
            $"Found {partial.Count} partial capture(s). Newest: {newest.TransactionId} "
            + (newest.Error is null ? "(no error recorded)" : $"— {newest.Error}");
    }

    private void OnDismissRecovery(object sender, RoutedEventArgs e)
        => WorkspaceState.Current.RecoveryBanner = null;

    private void OnOpenProject(object sender, ExecutedRoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { InitialDirectory = _project.RootDir };
        if (dlg.ShowDialog() != true) return;
        _project = new ProjectService(dlg.FolderName);
        _query = new QueryService(_project);
        WorkspaceState.Current.ProjectRoot = dlg.FolderName;
        _shell = new ShellViewModel(_project, _query);
        DataContext = _shell;
        CheckForRecoverableCaptures();
        StatusText.Text = $"Opened {dlg.FolderName}";
    }

    private async void OnOpenModel(object sender, ExecutedRoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open model (safetensors, gguf)",
            Filter = "Model files|*.safetensors;*.gguf|All|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            // 1) Local introspection populates the Overview pane without needing a worker.
            var d = new ModelIntrospectionService(new ArchitectureRegistry()).Introspect(dlg.FileName);
            var vm = _shell.OverviewVm;
            vm.ModelName    = d.DisplayName;
            vm.Architecture = d.ArchitectureName;
            vm.NLayers      = d.Layers.NumLayers;
            vm.NHeads       = d.Layers.NumHeads;
            vm.HiddenSize   = d.Layers.HiddenSize;
            vm.VocabSize    = d.Tokenizer?.VocabSize ?? 0;
            StatusText.Text = $"Introspected {d.DisplayName} ({d.ArchitectureName}) — loading on worker…";

            // 2) Coordinator.LoadModel — actually load the weights on the
            //    selected device. Reply carries resolved_device (the
            //    accelerator the worker actually landed on) which may
            //    differ from the request (llama.cpp falls back to CPU
            //    when the requested backend wasn't compiled in).
            using var chan = Grpc.Net.Client.GrpcChannel.ForAddress(
                Environment.GetEnvironmentVariable("STACKSCOPE_COORDINATOR_ENDPOINT")
                    ?? "http://127.0.0.1:50600");
            var coord = new StackScope.Proto.V1.Coordinator.CoordinatorClient(chan);
            var workers = await coord.ListWorkersAsync(new StackScope.Proto.V1.ListWorkersRequest());
            string workerId;
            if (workers.Workers.Count == 0)
            {
                var kind = dlg.FileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                    ? "llamacpp" : "pytorch";
                // Seed device_hint from the current dropdown pick so the
                // freshly-spawned worker already prefers that device on
                // its first LoadModel (env-driven, via WorkerLauncher).
                var started = await coord.StartWorkerAsync(new StackScope.Proto.V1.StartWorkerRequest
                { Kind = kind, DeviceHint = WorkspaceState.Current.SelectedDevice ?? "" });
                workerId = started.Worker.WorkerId;
            }
            else workerId = workers.Workers[0].WorkerId;

            var reply = await coord.LoadModelAsync(new StackScope.Proto.V1.CoordLoadModelRequest
            {
                WorkerId       = workerId,
                ModelPath      = dlg.FileName,
                Format         = dlg.FileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ? "gguf" : "safetensors",
                Device         = WorkspaceState.Current.SelectedDevice ?? "",
                TrustRemoteCode = false,
                NCtx           = 4096,
            });

            WorkspaceState.Current.CurrentModelHandle = reply.ModelHandle;
            WorkspaceState.Current.ResolvedDevice     = reply.ResolvedDevice;
            WorkspaceState.Current.ResolvedDeviceVerified = reply.ResolvedDeviceVerified;
            StatusText.Text =
                $"Loaded {reply.Architecture} on {reply.ResolvedDevice}"
                + (reply.ResolvedDeviceVerified ? " (verified)" : " (requested, unverified)")
                + $" — handle {reply.ModelHandle}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Open Model", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "Open Model failed: " + ex.Message;
        }
    }

    private RunInferenceDialog? _runDialog;

    private void OnStartCapture(object sender, ExecutedRoutedEventArgs e)
    {
        _runDialog = new Views.RunInferenceDialog { Owner = this };
        // Bridge AnalysisView's ablation fields into the capture dialog so
        // typing "Layer 5 / Head 3" in the Analysis Lab actually causes
        // the capture to zero that head. Without this bridge the boxes
        // were display-only. -1/-1 means "no ablation".
        _runDialog.SeedAblation(_shell.AblationVm.AblateLayer,
                                _shell.AblationVm.AblateHead);
        var ok = _runDialog.ShowDialog();
        if (ok == true && _runDialog.TransactionId is not null)
        {
            StatusText.Text = $"Capture complete: {_runDialog.TransactionId}";
            SelectionState.Current.TransactionId = _runDialog.TransactionId;
            _shell.OverviewVm.TransactionId = _runDialog.TransactionId;
            _shell.OverviewVm.RefreshTransactionStats(_project, _query);

            // Auto-compare: if this capture ran with head ablation, find
            // the newest completed non-ablated capture of the same prompt
            // and seed Diff Mode so the user sees the head's contribution
            // in one click. If no baseline exists we say so out loud —
            // no silent no-op.
            var justRan = _project.ListTransactions()
                .FirstOrDefault(t => t.TransactionId == _runDialog.TransactionId);

            // Surface the capacity-ceiling badge for whichever worker
            // handled this run. Cleared here even for non-ablated runs
            // so switching workers between captures updates the top-bar
            // truthfully. Null-safe: no metadata row means no badge.
            WorkspaceState.Current.CaptureCeiling = justRan?.CaptureCeiling;

            if (justRan is not null && justRan.WasAblated)
            {
                var baseline = _project.FindLatestNonAblatedBaseline(justRan);
                if (baseline is not null)
                {
                    // Honour the AnalysisView toggles: side order + sigma
                    // threshold seed. Without these the auto-diff would
                    // silently override whatever the user configured
                    // right next to the ablation controls.
                    if (_shell.AblationVm.AutoCompareAblatedOnLeft)
                    {
                        _shell.CompareVm.LeftTransactionId  = justRan.TransactionId;
                        _shell.CompareVm.RightTransactionId = baseline.TransactionId;
                    }
                    else
                    {
                        _shell.CompareVm.LeftTransactionId  = baseline.TransactionId;
                        _shell.CompareVm.RightTransactionId = justRan.TransactionId;
                    }
                    _shell.CompareVm.SigmaThreshold = _shell.AblationVm.AutoCompareSigma;
                    _shell.LibraryVm.Refresh();
                    // Kick off the diff asynchronously (RunCommand is an
                    // IAsyncRelayCommand) and swap the active pane so
                    // the user sees the ranked table populate live.
                    _shell.CompareVm.RunCommand.Execute(null);
                    FocusPane("compare");
                    var order = _shell.AblationVm.AutoCompareAblatedOnLeft
                                ? "ablated ⇆ baseline" : "baseline ⇆ ablated";
                    StatusText.Text =
                        $"Ablated capture {justRan.TransactionId} — auto-diffing "
                        + $"({order}) against baseline {baseline.TransactionId} "
                        + $"at {_shell.CompareVm.SigmaThreshold:F2}σ "
                        + $"(L{justRan.AblateLayer} H{justRan.AblateHead}).";
                }
                else
                {
                    FocusPane("overview");
                    StatusText.Text =
                        $"Ablated capture done ({justRan.TransactionId}). "
                        + "No non-ablated baseline of this prompt found — "
                        + "run the same prompt with L/H = -1/-1 to enable auto-compare.";
                }
            }
            else
            {
                FocusPane("overview");
            }
        }
        else StatusText.Text = "Capture cancelled or produced no transaction.";
        _runDialog = null;
    }

    private void OnStopCapture(object sender, ExecutedRoutedEventArgs e)
    {
        if (_runDialog is not null)
        {
            // The dialog owns its CancellationTokenSource; ask it to close.
            _runDialog.Close();
            StatusText.Text = "Capture stop requested.";
        }
        else StatusText.Text = "No capture is running.";
    }
    private void OnBackSelection(object sender, ExecutedRoutedEventArgs e) => SelectionState.Current.GoBack();
    private void OnForwardSelection(object sender, ExecutedRoutedEventArgs e) => SelectionState.Current.GoForward();
    private void OnDisclosureSimple(object sender, ExecutedRoutedEventArgs e)   => WorkspaceState.Current.Disclosure = DisclosureMode.Simple;
    private void OnDisclosureAdvanced(object sender, ExecutedRoutedEventArgs e) => WorkspaceState.Current.Disclosure = DisclosureMode.Advanced;
    private void OnDisclosureForensic(object sender, ExecutedRoutedEventArgs e) => WorkspaceState.Current.Disclosure = DisclosureMode.Forensic;

    private void OnOpenPalette(object sender, ExecutedRoutedEventArgs e)
        => new Views.CommandPaletteWindow { Owner = this }.ShowDialog();

    private void OnSaveLayout(object sender, ExecutedRoutedEventArgs e)
    {
        var path = Path.Combine(_project.LayoutsDir, "current.xml");
        using var writer = new StreamWriter(path);
        new XmlLayoutSerializer(Dock).Serialize(writer);
        StatusText.Text = $"Layout saved: {path}";
    }

    private void OnResetLayout(object sender, ExecutedRoutedEventArgs e)
    {
        var path = Path.Combine(_project.LayoutsDir, "current.xml");
        if (File.Exists(path)) File.Delete(path);
        var uri = new Uri("/StackScope;component/MainWindow.xaml", UriKind.Relative);
        var freshWindow = (Window)Application.LoadComponent(uri);
        var freshDock = ((MainWindow)freshWindow).Dock;
        Dock.Layout = freshDock.Layout;
        StatusText.Text = "Layout reset.";
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Ctrl+Alt+Shift+P — pin whatever the Compare view has loaded into
    /// the persistent Pin Board, then open the Pin Board pane so the
    /// user sees the new row. Delegates the "is there anything to pin"
    /// guard to <see cref="PinnedDiffsViewModel.PinCurrent"/> which
    /// updates its Status message instead of throwing.
    /// </summary>
    private void OnPinCurrentDiff(object sender, ExecutedRoutedEventArgs e)
    {
        _shell.PinBoardVm.PinCurrentCommand.Execute(null);
        FocusPane("pinboard");
        StatusText.Text = _shell.PinBoardVm.Status;
    }

    private void OnFocusPane(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is not string contentId) return;
        FocusPane(contentId);
    }

    private void FocusPane(string contentId)
    {
        var layout = Dock.Layout;
        foreach (var doc in layout.Descendents().OfType<AvalonDock.Layout.LayoutDocument>())
        {
            if (string.Equals(doc.ContentId, contentId, StringComparison.Ordinal))
            {
                doc.IsActive = true; doc.IsSelected = true; return;
            }
        }
        foreach (var anch in layout.Descendents().OfType<AvalonDock.Layout.LayoutAnchorable>())
        {
            if (string.Equals(anch.ContentId, contentId, StringComparison.Ordinal))
            {
                anch.Show(); anch.IsActive = true; return;
            }
        }
        StatusText.Text = $"Pane not found: {contentId}";
    }

    /// <summary>"Debug this token" — F4 shortcut. Reads the current
    /// selection's token id, opens the Attribution Graph view, seeds
    /// it with the current transaction + token, and runs the graph.
    /// One click == six views' worth of context.</summary>
    private void OnDebugToken(object sender, ExecutedRoutedEventArgs e)
    {
        var s = SelectionState.Current;
        var txid = WorkspaceState.Current.CurrentTransactionId;
        if (string.IsNullOrWhiteSpace(txid) || s.TokenIndex < 0)
        {
            StatusText.Text = "Select an event with a token first.";
            return;
        }
        _shell.AttributionVm.TransactionId = txid!;
        _shell.AttributionVm.TargetToken = s.TokenIndex;
        _shell.AttributionVm.BuildCommand.Execute(null);
        _shell.HealthVm.TransactionId = txid!;
        _shell.HealthVm.ComputeCommand.Execute(null);
        FocusPane("attribution");
        StatusText.Text = $"Debugging token {s.TokenIndex} in {txid}.";
    }
}
