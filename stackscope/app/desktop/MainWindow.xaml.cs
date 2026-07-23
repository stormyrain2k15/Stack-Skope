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

    private void OnOpenModel(object sender, ExecutedRoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open model (safetensors, gguf)",
            Filter = "Model files|*.safetensors;*.gguf|All|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var d = new ModelIntrospectionService(new ArchitectureRegistry()).Introspect(dlg.FileName);
            var vm = _shell.OverviewVm;
            vm.ModelName    = d.DisplayName;
            vm.Architecture = d.ArchitectureName;
            vm.NLayers      = d.Layers.NumLayers;
            vm.NHeads       = d.Layers.NumHeads;
            vm.HiddenSize   = d.Layers.HiddenSize;
            vm.VocabSize    = d.Tokenizer?.VocabSize ?? 0;
            WorkspaceState.Current.CurrentModelHandle = d.DisplayName;
            StatusText.Text = $"Loaded {d.DisplayName} ({d.ArchitectureName})";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Open Model", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnStartCapture(object sender, ExecutedRoutedEventArgs e)
        => StatusText.Text = "Capture requires a running coordinator/worker. See docs/CAPTURE.md.";
    private void OnStopCapture(object sender, ExecutedRoutedEventArgs e)
        => StatusText.Text = "Capture stop.";
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
}
