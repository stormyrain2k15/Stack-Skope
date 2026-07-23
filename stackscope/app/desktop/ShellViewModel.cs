using CommunityToolkit.Mvvm.ComponentModel;
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
    }
}
