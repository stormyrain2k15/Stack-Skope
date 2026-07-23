using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using StackScope.Adapters.Architectures;
using StackScope.Desktop.State;
using StackScope.Desktop.ViewModels;
using StackScope.Services;

namespace StackScope.Desktop;

/// <summary>
/// Application shell. Owns the top-level ViewModel graph and wires the
/// menu/command surface to the services layer. No business logic lives
/// here — this file only marshals user intent into service calls.
/// </summary>
public partial class MainWindow : Window
{
    private ProjectService _project;
    private QueryService _query;

    public MainWindow()
    {
        InitializeComponent();

        var projectRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StackScope", "default");
        _project = new ProjectService(projectRoot);
        _query   = new QueryService(_project);
        WorkspaceState.Current.ProjectRoot = projectRoot;

        DataContext = new ShellViewModel(_project, _query);

        SelectionState.Current.PropertyChanged += (_, __) => UpdateInspector();
        UpdateInspector();
    }

    private void UpdateInspector()
    {
        if (DataContext is ShellViewModel s)
        {
            s.InspectorEventId = SelectionState.Current.EventId?.ToString() ?? "—";
            s.InspectorKind    = SelectionState.Current.Kind?.ToString() ?? "—";
            s.InspectorToken   = SelectionState.Current.TokenIndex.ToString();
            s.InspectorLayer   = SelectionState.Current.LayerIndex.ToString();
            s.InspectorHead    = SelectionState.Current.HeadIndex.ToString();
        }
    }

    // ---------- Command handlers ----------------------------------------

    private void OnOpenProject(object sender, ExecutedRoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Open StackScope project folder",
            InitialDirectory = _project.RootDir
        };
        if (dlg.ShowDialog() == true)
        {
            _project = new ProjectService(dlg.FolderName);
            _query = new QueryService(_project);
            WorkspaceState.Current.ProjectRoot = dlg.FolderName;
            DataContext = new ShellViewModel(_project, _query);
            StatusText.Text = $"Opened project: {dlg.FolderName}";
        }
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
            var introspect = new ModelIntrospectionService(new ArchitectureRegistry());
            var descriptor = introspect.Introspect(dlg.FileName);
            var s = (ShellViewModel)DataContext;
            s.OverviewVm.ModelName    = descriptor.DisplayName;
            s.OverviewVm.Architecture = descriptor.ArchitectureName;
            s.OverviewVm.NLayers      = descriptor.Layers.NumLayers;
            s.OverviewVm.NHeads       = descriptor.Layers.NumHeads;
            s.OverviewVm.HiddenSize   = descriptor.Layers.HiddenSize;
            s.OverviewVm.VocabSize    = descriptor.Tokenizer?.VocabSize ?? 0;
            WorkspaceState.Current.CurrentModelHandle = descriptor.DisplayName;
            StatusText.Text = $"Loaded {descriptor.DisplayName} ({descriptor.ArchitectureName}).";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Open Model", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnStartCapture(object sender, ExecutedRoutedEventArgs e)
    {
        StatusText.Text = "Capture start requested. Coordinator wiring belongs on the Windows box.";
    }

    private void OnStopCapture(object sender, ExecutedRoutedEventArgs e)
    {
        StatusText.Text = "Capture stop requested.";
    }

    private void OnBackSelection(object sender, ExecutedRoutedEventArgs e)
        => SelectionState.Current.GoBack();
    private void OnForwardSelection(object sender, ExecutedRoutedEventArgs e)
        => SelectionState.Current.GoForward();

    private void OnDisclosureSimple(object sender, ExecutedRoutedEventArgs e)
        => WorkspaceState.Current.Disclosure = DisclosureMode.Simple;
    private void OnDisclosureAdvanced(object sender, ExecutedRoutedEventArgs e)
        => WorkspaceState.Current.Disclosure = DisclosureMode.Advanced;
    private void OnDisclosureForensic(object sender, ExecutedRoutedEventArgs e)
        => WorkspaceState.Current.Disclosure = DisclosureMode.Forensic;

    private void OnOpenPalette(object sender, ExecutedRoutedEventArgs e)
    {
        var w = new Views.CommandPaletteWindow { Owner = this };
        w.ShowDialog();
    }

    private void OnSaveLayout(object sender, ExecutedRoutedEventArgs e)
    {
        var path = Path.Combine(_project.LayoutsDir, "current.xml");
        using var writer = new StreamWriter(path);
        new AvalonDock.Layout.Serialization.XmlLayoutSerializer(Dock).Serialize(writer);
        StatusText.Text = $"Saved layout to {path}.";
    }

    private void OnResetLayout(object sender, ExecutedRoutedEventArgs e)
    {
        // Reload the built-in default layout — this preserves the XAML-declared arrangement.
        // Users override with saved layouts via SaveLayout.
        StatusText.Text = "Reset layout (default).";
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();
}
