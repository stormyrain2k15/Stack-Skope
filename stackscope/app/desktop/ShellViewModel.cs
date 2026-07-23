using CommunityToolkit.Mvvm.ComponentModel;
using StackScope.Desktop.Services;
using StackScope.Desktop.State;
using StackScope.Desktop.ViewModels;
using StackScope.Services;

namespace StackScope.Desktop;

public sealed partial class ShellViewModel : ObservableObject
{
    public OverviewViewModel       OverviewVm    { get; }
    public TokensViewModel         TokensVm      { get; }
    public LayersViewModel         LayersVm      { get; }
    public AttentionViewModel      AttentionVm   { get; }
    public ActivationsViewModel    ActivationsVm { get; }
    public TensorsViewModel        TensorsVm     { get; }
    public DriverViewModel         DriverVm      { get; }
    public KernelsViewModel        KernelsVm     { get; }
    public MemoryViewModel         MemoryVm      { get; }
    public TimelineViewModel       TimelineVm    { get; }
    public CompareDiffViewModel    CompareVm     { get; }
    public CaptureLibraryViewModel LibraryVm     { get; }
    public ProjectTreeViewModel    ProjectTreeVm { get; }
    public DeviceSelectorViewModel DeviceVm      { get; }
    public AttentionHeatmapViewModel HeatmapVm   { get; }
    public KvCacheViewModel        KvCacheVm     { get; }
    public DivergenceViewModel     DivergenceVm  { get; }
    public CircuitTraceViewModel   CircuitVm     { get; }
    public AblationViewModel       AblationVm    { get; }

    // Product tools — all one-click, all backed by real code.
    public HooksInspectorViewModel      HooksInspectorVm { get; }
    public BundleWorkbenchViewModel     BundleVm         { get; }
    public LiveTailViewModel            LiveTailVm       { get; }
    public MCPServerViewModel           McpVm            { get; }
    public AttachSessionViewModel       AttachVm         { get; }
    public ReproDiffViewModel           ReproDiffVm      { get; }
    public BugReportExporterViewModel   BugReportVm      { get; }

    // Analysis passes.
    public HealthDashboardViewModel     HealthVm         { get; }
    public QuantizationDiffViewModel    QuantVm          { get; }
    public DeterminismAuditorViewModel  DeterminismVm    { get; }
    public AttributionGraphViewModel    AttributionVm    { get; }
    public AnnotationsViewModel         NotesVm          { get; }
    public NaturalQueryViewModel        NaturalQueryVm   { get; }
    public PinnedDiffsViewModel         PinBoardVm       { get; }

    [ObservableProperty] private string inspectorEventId = "—";
    [ObservableProperty] private string inspectorKind    = "—";
    [ObservableProperty] private string inspectorToken   = "-1";
    [ObservableProperty] private string inspectorLayer   = "-1";
    [ObservableProperty] private string inspectorHead    = "-1";

    public string?       ProjectRoot          => WorkspaceState.Current.ProjectRoot;
    public string?       CurrentModelHandle   => WorkspaceState.Current.CurrentModelHandle;
    public string?       CurrentTransactionId => WorkspaceState.Current.CurrentTransactionId;
    public DisclosureMode Disclosure          => WorkspaceState.Current.Disclosure;
    public string?       RecoveryBanner       => WorkspaceState.Current.RecoveryBanner;
    public bool          HasRecoveryBanner    => !string.IsNullOrEmpty(WorkspaceState.Current.RecoveryBanner);
    public string?       CaptureCeiling       => WorkspaceState.Current.CaptureCeiling;
    public bool          HasCaptureCeiling    => !string.IsNullOrEmpty(WorkspaceState.Current.CaptureCeiling);

    public ShellViewModel(ProjectService project, QueryService query)
    {
        OverviewVm    = new OverviewViewModel();
        TokensVm      = new TokensViewModel(query);
        LayersVm      = new LayersViewModel(query);
        AttentionVm   = new AttentionViewModel(query);
        ActivationsVm = new ActivationsViewModel(query);
        TensorsVm     = new TensorsViewModel(query);
        DriverVm      = new DriverViewModel(query);
        KernelsVm     = new KernelsViewModel(query);
        MemoryVm      = new MemoryViewModel(query);
        TimelineVm    = new TimelineViewModel(query);
        CompareVm     = new CompareDiffViewModel(project);
        LibraryVm     = new CaptureLibraryViewModel(project);
        ProjectTreeVm = new ProjectTreeViewModel(project);
        DeviceVm      = new DeviceSelectorViewModel();
        HeatmapVm     = new AttentionHeatmapViewModel(project);
        KvCacheVm     = new KvCacheViewModel(project);
        DivergenceVm  = new DivergenceViewModel(project);
        CircuitVm     = new CircuitTraceViewModel(project);
        AblationVm    = new AblationViewModel();

        // Product tools — the CLI stays intact for automation, but the
        // UI is the primary surface. Every tool is one button-click away.
        var py = new PythonCli();
        var ps = new PowerShellRunner();
        HooksInspectorVm = new HooksInspectorViewModel(py);
        BundleVm         = new BundleWorkbenchViewModel(py);
        LiveTailVm       = new LiveTailViewModel(py);
        McpVm            = new MCPServerViewModel(py, ps);
        AttachVm         = new AttachSessionViewModel(ps);
        ReproDiffVm      = new ReproDiffViewModel(py);
        BugReportVm      = new BugReportExporterViewModel(py, ps);

        HealthVm         = new HealthDashboardViewModel(project);
        QuantVm          = new QuantizationDiffViewModel(project);
        DeterminismVm    = new DeterminismAuditorViewModel(project);
        AttributionVm    = new AttributionGraphViewModel(project);
        NotesVm          = new AnnotationsViewModel(project);
        NaturalQueryVm   = new NaturalQueryViewModel();
        PinBoardVm       = new PinnedDiffsViewModel(project, CompareVm);

        DetectDevicesCommand = new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(DetectDevicesAsync);
    }

    public CommunityToolkit.Mvvm.Input.IAsyncRelayCommand DetectDevicesCommand { get; }

    /// <summary>
    /// Ask the current worker (or a freshly-started pytorch worker if
    /// none exists) for its available accelerators and populate the
    /// device dropdown. Called on the Detect button in the top bar.
    /// </summary>
    public async Task DetectDevicesAsync()
    {
        try
        {
            using var chan = Grpc.Net.Client.GrpcChannel.ForAddress(
                Environment.GetEnvironmentVariable("STACKSCOPE_COORDINATOR_ENDPOINT")
                    ?? "http://127.0.0.1:50600");
            var client = new StackScope.Proto.V1.Coordinator.CoordinatorClient(chan);

            // Find or start a worker.
            var list = await client.ListWorkersAsync(new StackScope.Proto.V1.ListWorkersRequest());
            string workerId;
            if (list.Workers.Count == 0)
            {
                // Pass the currently-selected device (if any) so the newly
                // launched worker starts already aware of the user's
                // preferred accelerator. Empty string ⇒ worker picks its
                // own default.
                var started = await client.StartWorkerAsync(new StackScope.Proto.V1.StartWorkerRequest
                { Kind = "pytorch",
                  DeviceHint = WorkspaceState.Current.SelectedDevice ?? "" });
                workerId = started.Worker.WorkerId;
            }
            else workerId = list.Workers[0].WorkerId;

            var reply = await client.ListDevicesAsync(new StackScope.Proto.V1.ListDevicesRequest
            { WorkerId = workerId });

            var infos = reply.Devices.Select(d => new StackScope.Adapters.Runtimes.DeviceInfo(
                d.Id, d.Kind, d.Name, d.TotalMemoryBytes, d.FreeMemoryBytes,
                d.ComputeCapability, d.DriverVersion, d.MultiProcessorCount,
                d.IsIntegrated, d.IsDefault)).ToList();
            DeviceVm.ReportDiscoveredDevices(infos);
        }
        catch (Exception ex)
        {
            DeviceVm.DetectStatus = "Detect failed: " + ex.Message;
        }
    }

    public void RefreshInspectorFromSelection()
    {
        var s = SelectionState.Current;
        InspectorEventId = s.EventId?.ToString() ?? "—";
        InspectorKind    = s.Kind?.ToString() ?? "—";
        InspectorToken   = s.TokenIndex.ToString();
        InspectorLayer   = s.LayerIndex.ToString();
        InspectorHead    = s.HeadIndex.ToString();
    }

    public void NotifyWorkspaceChanged()
    {
        OnPropertyChanged(nameof(ProjectRoot));
        OnPropertyChanged(nameof(CurrentModelHandle));
        OnPropertyChanged(nameof(CurrentTransactionId));
        OnPropertyChanged(nameof(Disclosure));
        OnPropertyChanged(nameof(RecoveryBanner));
        OnPropertyChanged(nameof(HasRecoveryBanner));
        OnPropertyChanged(nameof(CaptureCeiling));
        OnPropertyChanged(nameof(HasCaptureCeiling));
    }
}
