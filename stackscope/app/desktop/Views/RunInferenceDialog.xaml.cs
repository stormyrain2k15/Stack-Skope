using System.Threading;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using StackScope.Desktop.State;
using StackScope.Proto.V1;

namespace StackScope.Desktop.Views;

public sealed partial class RunInferenceDialogViewModel : ObservableObject
{
    [ObservableProperty] private string _prompt = "The quick brown fox";
    [ObservableProperty] private int    _maxNewTokens = 32;
    [ObservableProperty] private float  _temperature = 0.7f;
    [ObservableProperty] private float  _topP = 0.95f;
    [ObservableProperty] private int    _topK = 40;
    [ObservableProperty] private ulong  _seed = 0;
    [ObservableProperty] private int    _ablateLayer = -1;
    [ObservableProperty] private int    _ablateHead  = -1;
    [ObservableProperty] private string _status = "";

    public string DeviceLabel =>
        WorkspaceState.Current.SelectedDevice ?? "cpu (no worker yet)";

    /// <summary>
    /// Set by <see cref="RunInferenceDialog.OnStart"/> after LoadModel
    /// resolves — shows the *actual* device the model landed on, which
    /// may differ from the request (llama.cpp falls back to CPU if a
    /// backend isn't compiled in).
    /// </summary>
    [ObservableProperty] private string _resolvedDevice = "";
}

public partial class RunInferenceDialog : Window
{
    private readonly RunInferenceDialogViewModel _vm = new();
    private CancellationTokenSource? _cts;
    public string? TransactionId { get; private set; }

    public RunInferenceDialog() { InitializeComponent(); DataContext = _vm; }

    /// <summary>
    /// Seed the dialog with an ablation layer/head coming from another
    /// view (e.g. the Analysis Lab "Attention head ablation" section).
    /// Without this the AnalysisView fields would be display-only —
    /// the user would think they had queued an ablation but the
    /// dialog's own defaults (-1/-1) would win. Called by
    /// MainWindow.OnStartCapture before ShowDialog().
    /// </summary>
    public void SeedAblation(int layer, int head)
    {
        _vm.AblateLayer = layer;
        _vm.AblateHead  = head;
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        var handle = WorkspaceState.Current.CurrentModelHandle;
        if (string.IsNullOrWhiteSpace(handle))
        {
            _vm.Status = "Load a model first (File → Open Model…).";
            return;
        }
        _cts = new CancellationTokenSource();
        try
        {
            using var chan = Grpc.Net.Client.GrpcChannel.ForAddress(
                Environment.GetEnvironmentVariable("STACKSCOPE_COORDINATOR_ENDPOINT")
                    ?? "http://127.0.0.1:50600");
            var client = new Coordinator.CoordinatorClient(chan);
            var workers = await client.ListWorkersAsync(new ListWorkersRequest());
            if (workers.Workers.Count == 0)
            {
                _vm.Status = "No worker running. Start a pytorch worker first.";
                return;
            }
            var workerId = workers.Workers[0].WorkerId;

            var call = client.RunInference(new CoordRunRequest
            {
                WorkerId = workerId,
                ModelHandle = handle,
                Prompt = _vm.Prompt,
                MaxNewTokens = _vm.MaxNewTokens,
                Temperature = _vm.Temperature,
                TopP = _vm.TopP,
                TopK = _vm.TopK,
                Seed = _vm.Seed,
                CaptureLevel = (CaptureLevel)(int)WorkspaceState.Current.Disclosure,
                AblateLayer = _vm.AblateLayer,
                AblateHead  = _vm.AblateHead,
            }, cancellationToken: _cts.Token);

            await foreach (var progress in call.ResponseStream.ReadAllAsync(_cts.Token))
            {
                _vm.Status = $"{progress.EventsCommitted} events · {progress.TokensEmitted} tokens";
                TransactionId = progress.TransactionId;
                if (progress.Finished)
                {
                    if (!string.IsNullOrEmpty(progress.Error))
                        _vm.Status = $"Finished with error: {progress.Error}";
                    else
                        _vm.Status = $"Done — transaction {progress.TransactionId}";
                    WorkspaceState.Current.CurrentTransactionId = progress.TransactionId;
                    DialogResult = true;
                    Close();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { _vm.Status = "Cancelled."; }
        catch (Exception ex) { _vm.Status = "Failed: " + ex.Message; }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        DialogResult = false;
        Close();
    }
}
