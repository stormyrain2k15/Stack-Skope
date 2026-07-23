using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StackScope.Adapters.Runtimes;
using StackScope.Desktop.State;

namespace StackScope.Desktop.ViewModels;

/// <summary>
/// Device selector — the UI dropdown of accelerators available to
/// StackScope. The coordinator queries the worker's rich Capabilities
/// on startup and calls <see cref="ReportDiscoveredDevices"/> with a
/// list of <see cref="DeviceInfo"/> that includes name, VRAM, compute
/// capability, and driver version.
///
/// The dropdown shows the human-friendly label
/// (e.g. "cuda:0 · NVIDIA RTX 4090 · 24 GB · 8.9"). Whatever the user
/// picks is persisted to <see cref="WorkspaceState.SelectedDevice"/>
/// and forwarded on every subsequent <c>LoadModel</c> RPC.
/// </summary>
public sealed partial class DeviceSelectorViewModel : ObservableObject
{
    public ObservableCollection<DeviceInfo> Devices { get; } = new();

    [ObservableProperty] private DeviceInfo? _selected;
    [ObservableProperty] private string _detectStatus = "Not detected yet.";

    public string? SelectedDeviceId => Selected?.Id;

    public DeviceSelectorViewModel()
    {
        // Immediate CPU fallback so the dropdown is never empty on
        // first paint. Replaced when the coordinator reports real
        // devices back.
        var cpu = new DeviceInfo("cpu", "cpu", "CPU (waiting on worker)",
                                 0, 0, "", "", 0, false, true);
        Devices.Add(cpu);
        Selected = cpu;
    }

    public void ReportDiscoveredDevices(IReadOnlyList<DeviceInfo> discovered)
    {
        var currentId = Selected?.Id;
        Devices.Clear();
        foreach (var d in discovered) Devices.Add(d);
        if (Devices.Count == 0)
        {
            DetectStatus = "No devices reported.";
            return;
        }
        Selected = discovered.FirstOrDefault(d => d.Id == currentId)
                   ?? discovered.FirstOrDefault(d => d.IsDefault)
                   ?? discovered[0];
        DetectStatus = $"Detected {Devices.Count} device(s). Default: {Selected?.Id}";
    }

    partial void OnSelectedChanged(DeviceInfo? value)
    {
        WorkspaceState.Current.SelectedDevice = value?.Id;
    }
}
