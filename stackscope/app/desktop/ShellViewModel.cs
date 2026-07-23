using CommunityToolkit.Mvvm.ComponentModel;
using StackScope.Desktop.ViewModels;
using StackScope.Services;

namespace StackScope.Desktop;

/// <summary>
/// Root ViewModel bound to <see cref="MainWindow"/>. Owns one instance
/// of each per-view VM so they retain their state as the user tabs
/// around the docking workspace.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public OverviewViewModel        OverviewVm   { get; }
    public TokensViewModel          TokensVm     { get; }
    public LayersViewModel          LayersVm     { get; }
    public AttentionViewModel       AttentionVm  { get; }
    public ActivationsViewModel     ActivationsVm{ get; }
    public TensorsViewModel         TensorsVm    { get; }
    public DriverViewModel          DriverVm     { get; }
    public KernelsViewModel         KernelsVm    { get; }
    public MemoryViewModel          MemoryVm     { get; }
    public TimelineViewModel        TimelineVm   { get; }
    public CompareViewModel         CompareVm    { get; }
    public CaptureLibraryViewModel  LibraryVm    { get; }

    // Bound to the inspector pane.
    [ObservableProperty] private string inspectorEventId = "—";
    [ObservableProperty] private string inspectorKind    = "—";
    [ObservableProperty] private string inspectorToken   = "-1";
    [ObservableProperty] private string inspectorLayer   = "-1";
    [ObservableProperty] private string inspectorHead    = "-1";
    [ObservableProperty] private string inspectorConfidenceLabel = "Not correlated";
    [ObservableProperty] private System.Windows.Media.Brush inspectorConfidenceBrush =
        (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Brush.Text.Muted"];

    public string?    ProjectRoot            => State.WorkspaceState.Current.ProjectRoot;
    public string?    CurrentModelHandle     => State.WorkspaceState.Current.CurrentModelHandle;
    public string?    CurrentTransactionId   => State.WorkspaceState.Current.CurrentTransactionId;
    public State.DisclosureMode Disclosure   => State.WorkspaceState.Current.Disclosure;

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
        CompareVm     = new CompareViewModel();
        LibraryVm     = new CaptureLibraryViewModel(project);

        State.WorkspaceState.Current.PropertyChanged += (_, __) =>
        {
            OnPropertyChanged(nameof(ProjectRoot));
            OnPropertyChanged(nameof(CurrentModelHandle));
            OnPropertyChanged(nameof(CurrentTransactionId));
            OnPropertyChanged(nameof(Disclosure));
        };
    }
}
