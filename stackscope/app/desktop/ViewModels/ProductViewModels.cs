using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StackScope.Desktop.Services;

namespace StackScope.Desktop.ViewModels;

/// <summary>
/// ViewModels for all one-click product surfaces that shell out to
/// Python CLIs. Each VM owns its own state and exposes a single
/// verb-named async command; no user ever needs to open a terminal.
///
/// The Python CLIs remain valid for automation, but the UI is the
/// primary surface — click a button, get an answer.
/// </summary>

// ---------- Hooks Inspector (was: `python -m ... dry_run`) ------------

public sealed partial class HooksInspectorViewModel : ObservableObject
{
    private readonly PythonCli _py;

    [ObservableProperty] private string _model = "tiny";
    [ObservableProperty] private string _output = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "";

    public ObservableCollection<string> Presets { get; } =
        new(new[] { "tiny", "llama", "mistral", "qwen", "gemma", "gpt2" });

    public HooksInspectorViewModel(PythonCli py) { _py = py; }

    public IAsyncRelayCommand InspectCommand => new AsyncRelayCommand(InspectAsync);

    private async Task InspectAsync()
    {
        IsBusy = true; Status = "Inspecting…"; Output = "";
        try
        {
            var args = Model.StartsWith("hf:", StringComparison.OrdinalIgnoreCase)
                ? new[] { "--hf", Model.Substring(3) }
                : new[] { "--model", Model };
            Output = await _py.CaptureAsync("stackscope_worker.dry_run", args);
            Status = "Done.";
        }
        catch (Exception ex) { Status = $"Failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }
}

// ---------- Bundle Workbench (pack + unpack + verify) -----------------

public sealed partial class BundleWorkbenchViewModel : ObservableObject
{
    private readonly PythonCli _py;

    [ObservableProperty] private string _captureDir = "";
    [ObservableProperty] private string _bundlePath = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _verifyReport = "";

    public BundleWorkbenchViewModel(PythonCli py) { _py = py; }

    public IAsyncRelayCommand PackCommand   => new AsyncRelayCommand(PackAsync);
    public IAsyncRelayCommand UnpackCommand => new AsyncRelayCommand(UnpackAsync);
    public IAsyncRelayCommand VerifyCommand => new AsyncRelayCommand(VerifyAsync);
    public ICommand           OpenFolderCmd  => new RelayCommand<string>(p => PowerShellRunner.OpenInExplorer(p ?? ""));

    private async Task PackAsync()
    {
        if (string.IsNullOrWhiteSpace(CaptureDir) || string.IsNullOrWhiteSpace(BundlePath)) return;
        IsBusy = true; Status = "Packing bundle…";
        try
        {
            await _py.CaptureAsync("stackscope_worker.bundle",
                new[] { "pack", CaptureDir, BundlePath, "--notes", Notes });
            Status = $"Wrote {BundlePath}";
        }
        catch (Exception ex) { Status = $"Pack failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task UnpackAsync()
    {
        if (string.IsNullOrWhiteSpace(BundlePath) || string.IsNullOrWhiteSpace(CaptureDir)) return;
        IsBusy = true; Status = "Unpacking…";
        try
        {
            await _py.CaptureAsync("stackscope_worker.bundle",
                new[] { "unpack", BundlePath, CaptureDir });
            Status = $"Unpacked into {CaptureDir}";
        }
        catch (Exception ex) { Status = $"Unpack failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private async Task VerifyAsync()
    {
        if (string.IsNullOrWhiteSpace(BundlePath)) return;
        IsBusy = true; Status = "Verifying…"; VerifyReport = "";
        try
        {
            var r = await _py.RunModuleAsync("stackscope_worker.repro",
                new[] { BundlePath, "--summary" });
            VerifyReport = r.StdOut + r.StdErr;
            Status = r.ExitCode == 0 ? "Bundle verified — no drift." : "DRIFT detected.";
        }
        catch (Exception ex) { Status = $"Verify failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }
}

// ---------- Live Tail (was: `stackscope-tail`) ------------------------

public sealed partial class LiveTailViewModel : ObservableObject
{
    private readonly PythonCli _py;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _capturePath = "";
    [ObservableProperty] private string _grep = "";
    [ObservableProperty] private string _kind = "";
    [ObservableProperty] private bool _isRunning;

    public ObservableCollection<string> Lines { get; } = new();

    public LiveTailViewModel(PythonCli py) { _py = py; }

    public IAsyncRelayCommand StartCommand => new AsyncRelayCommand(StartAsync);
    public IRelayCommand      StopCommand  => new RelayCommand(Stop);
    public IRelayCommand      ClearCommand => new RelayCommand(() => Lines.Clear());

    private async Task StartAsync()
    {
        if (IsRunning) return;
        if (string.IsNullOrWhiteSpace(CapturePath)) return;
        _cts = new CancellationTokenSource();
        var args = new List<string> { CapturePath };
        if (!string.IsNullOrWhiteSpace(Grep)) { args.Add("--grep"); args.Add(Grep); }
        if (!string.IsNullOrWhiteSpace(Kind)) { args.Add("--kind"); args.Add(Kind); }
        IsRunning = true;
        try
        {
            await _py.RunModuleAsync("stackscope_worker.tail", args,
                onStdOut: line => System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    Lines.Add(line);
                    while (Lines.Count > 5000) Lines.RemoveAt(0);
                }),
                ct: _cts.Token);
        }
        catch (OperationCanceledException) { /* expected */ }
        finally { IsRunning = false; _cts?.Dispose(); _cts = null; }
    }

    private void Stop() => _cts?.Cancel();
}

// ---------- MCP Server (start / stop / share endpoint) ---------------

public sealed partial class MCPServerViewModel : ObservableObject
{
    private readonly PythonCli _py;
    private readonly PowerShellRunner _ps;
    private System.Diagnostics.Process? _server;

    [ObservableProperty] private string _bundlePath = "";
    [ObservableProperty] private string _status = "Stopped";
    [ObservableProperty] private bool   _isRunning;

    public MCPServerViewModel(PythonCli py, PowerShellRunner ps) { _py = py; _ps = ps; }

    public IRelayCommand StartCommand => new RelayCommand(Start);
    public IRelayCommand StopCommand  => new RelayCommand(Stop);
    public IAsyncRelayCommand CopyClaudeConfigCommand => new AsyncRelayCommand(CopyClaudeConfigAsync);

    private void Start()
    {
        if (IsRunning || string.IsNullOrWhiteSpace(BundlePath)) return;
        var psi = new System.Diagnostics.ProcessStartInfo("python")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add("stackscope_worker.mcp_server");
        psi.ArgumentList.Add(BundlePath);
        _server = System.Diagnostics.Process.Start(psi);
        IsRunning = _server is not null && !_server.HasExited;
        Status = IsRunning ? $"Running (PID {_server!.Id})" : "Failed to start";
    }

    private void Stop()
    {
        try { if (_server is { HasExited: false }) _server.Kill(entireProcessTree: true); } catch { }
        _server = null; IsRunning = false; Status = "Stopped";
    }

    private async Task CopyClaudeConfigAsync()
    {
        var config = new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["stackscope"] = new
                {
                    command = "python",
                    args = new[] { "-m", "stackscope_worker.mcp_server", BundlePath },
                },
            },
        };
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        await _ps.CopyToClipboardAsync(json);
        Status = "Claude Desktop config copied to clipboard.";
    }
}

// ---------- Attach Session (in-process attach helper) -----------------

public sealed partial class AttachSessionViewModel : ObservableObject
{
    private readonly PowerShellRunner _ps;

    [ObservableProperty] private string _endpoint = "127.0.0.1:50501";
    [ObservableProperty] private string _snippet = "";
    [ObservableProperty] private string _status = "";

    public AttachSessionViewModel(PowerShellRunner ps)
    {
        _ps = ps;
        RegenerateSnippet();
    }

    partial void OnEndpointChanged(string value) => RegenerateSnippet();

    private void RegenerateSnippet()
    {
        Snippet =
            "# Paste this into your running Python (notebook cell, REPL, or startup):\n" +
            "from stackscope_worker.attach import attach_here\n" +
           $"attach_here(your_model, endpoint='{Endpoint}', block=False)\n" +
            "# Then click 'Connect' in StackScope and enter the endpoint above.";
    }

    public IAsyncRelayCommand CopySnippetCommand => new AsyncRelayCommand(async () =>
    {
        await _ps.CopyToClipboardAsync(Snippet);
        Status = "Snippet copied — paste into your Python process.";
    });
}

// ---------- Repro & Diff (bundle vs bundle) --------------------------

public sealed partial class ReproDiffViewModel : ObservableObject
{
    private readonly PythonCli _py;

    [ObservableProperty] private string _bundleA = "";
    [ObservableProperty] private string _bundleB = "";
    [ObservableProperty] private string _report = "";
    [ObservableProperty] private bool _isBusy;

    public ReproDiffViewModel(PythonCli py) { _py = py; }

    public IAsyncRelayCommand CompareCommand => new AsyncRelayCommand(CompareAsync);

    private async Task CompareAsync()
    {
        if (string.IsNullOrWhiteSpace(BundleA) || string.IsNullOrWhiteSpace(BundleB)) return;
        IsBusy = true; Report = "";
        try
        {
            var r = await _py.RunModuleAsync("stackscope_worker.repro",
                new[] { BundleA, "--against", BundleB, "--summary" });
            Report = r.StdOut + (string.IsNullOrEmpty(r.StdErr) ? "" : "\n" + r.StdErr);
        }
        finally { IsBusy = false; }
    }
}

// ---------- Bug Report Exporter (one-button ".stackscope for support") ---

public sealed partial class BugReportExporterViewModel : ObservableObject
{
    private readonly PythonCli _py;
    private readonly PowerShellRunner _ps;

    [ObservableProperty] private string _captureDir = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _outputPath = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isBusy;

    public BugReportExporterViewModel(PythonCli py, PowerShellRunner ps) { _py = py; _ps = ps; }

    public IAsyncRelayCommand ExportCommand => new AsyncRelayCommand(ExportAsync);
    public IRelayCommand      OpenFolderCommand => new RelayCommand(() =>
    {
        if (!string.IsNullOrEmpty(OutputPath) && File.Exists(OutputPath))
            PowerShellRunner.OpenInExplorer(Path.GetDirectoryName(OutputPath) ?? "");
    });

    private async Task ExportAsync()
    {
        IsBusy = true; Status = "Building manifest…";
        try
        {
            var manifest = await _py.CaptureAsync("stackscope_worker.manifest", new[] { "--emit" });
            var dir = string.IsNullOrWhiteSpace(CaptureDir) ? Path.GetTempPath() : CaptureDir;
            var target = string.IsNullOrWhiteSpace(OutputPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                               $"stackscope-report-{DateTime.Now:yyyyMMdd-HHmmss}.stackscope")
                : OutputPath;
            OutputPath = target;

            var manifestFile = Path.Combine(dir, "manifest.json");
            await File.WriteAllTextAsync(manifestFile, manifest);
            var notesFile = Path.Combine(dir, "notes.md");
            await File.WriteAllTextAsync(notesFile, Description ?? "");

            Status = "Packing…";
            await _py.CaptureAsync("stackscope_worker.bundle",
                new[] { "pack", dir, target, "--notes", Description ?? "" });
            Status = $"Ready. Attach {target} to your bug report.";
        }
        catch (Exception ex) { Status = $"Export failed: {ex.Message}"; }
        finally { IsBusy = false; }
    }
}
