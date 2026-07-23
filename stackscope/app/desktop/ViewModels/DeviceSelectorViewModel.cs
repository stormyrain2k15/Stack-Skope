using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StackScope.Desktop.State;

namespace StackScope.Desktop.ViewModels;

/// <summary>
/// Device selector — decouples UI GPU from inference GPU per the plan.
/// Devices are populated by the coordinator when it discovers workers;
/// the UI never assumes what's available.
/// </summary>
public sealed partial class DeviceSelectorViewModel : ObservableObject
{
    public ObservableCollection<string> Devices { get; } = new();
    [ObservableProperty] private string? selectedDevice;

    public DeviceSelectorViewModel()
    {
        // Static defaults visible immediately; the coordinator refreshes
        // this list after StartWorker() succeeds.
        Devices.Add("cpu");
        SelectedDevice = "cpu";
    }

    public void ReportDiscoveredDevices(IEnumerable<string> discovered)
    {
        var current = SelectedDevice;
        Devices.Clear();
        foreach (var d in discovered) if (!Devices.Contains(d)) Devices.Add(d);
        if (Devices.Count > 0)
            SelectedDevice = current is not null && Devices.Contains(current) ? current : Devices[0];
    }

    partial void OnSelectedDeviceChanged(string? value)
    {
        WorkspaceState.Current.SelectedDevice = value;
    }
}
