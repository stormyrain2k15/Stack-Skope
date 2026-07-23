using CommunityToolkit.Mvvm.ComponentModel;

namespace StackScope.Desktop.State;

/// <summary>
/// Progressive disclosure mode. The whole app respects the same enum:
/// panels hide / show columns and detail levels based on the current
/// mode (per project rule §38 — progressive disclosure enforced).
/// </summary>
public enum DisclosureMode
{
    Simple = 0,     // token + text output only
    Advanced = 1,   // + layer + attention + activations
    Forensic = 2    // + kernels + memory + tensors + timings
}

public sealed partial class WorkspaceState : ObservableObject
{
    public static WorkspaceState Current { get; } = new();

    [ObservableProperty] private DisclosureMode disclosure = DisclosureMode.Advanced;
    [ObservableProperty] private bool reducedMotion;
    [ObservableProperty] private string? projectRoot;
    [ObservableProperty] private string? currentTransactionId;
    [ObservableProperty] private string? currentModelHandle;
    [ObservableProperty] private string? selectedDevice;
    /// <summary>
    /// The device the loaded model *actually* landed on, as reported by the
    /// worker in <c>LoadModelReply.resolved_device</c>. Distinct from
    /// <see cref="SelectedDevice"/> (which is the user's request) so the
    /// UI can badge cases where llama.cpp fell back to CPU because the
    /// requested backend wasn't compiled in.
    /// </summary>
    [ObservableProperty] private string? resolvedDevice;
    /// <summary>
    /// True if the worker read placement back from llama.cpp's real
    /// per-layer device map (or torch's `.to(device)`). False when
    /// llama.cpp is older than mid-2024 and we're reflecting the
    /// request without confirmation. The UI badges this so nobody
    /// mistakes "asked" for "verified".
    /// </summary>
    [ObservableProperty] private bool resolvedDeviceVerified;
    [ObservableProperty] private string? recoveryBanner;

    /// <summary>Bindable helpers used by view visibility triggers.</summary>
    public bool ShowAdvanced => Disclosure >= DisclosureMode.Advanced;
    public bool ShowForensic => Disclosure >= DisclosureMode.Forensic;

    partial void OnDisclosureChanged(DisclosureMode value)
    {
        OnPropertyChanged(nameof(ShowAdvanced));
        OnPropertyChanged(nameof(ShowForensic));
    }
}
