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

    /// <summary>Bindable helpers used by view visibility triggers.</summary>
    public bool ShowAdvanced => Disclosure >= DisclosureMode.Advanced;
    public bool ShowForensic => Disclosure >= DisclosureMode.Forensic;

    partial void OnDisclosureChanged(DisclosureMode value)
    {
        OnPropertyChanged(nameof(ShowAdvanced));
        OnPropertyChanged(nameof(ShowForensic));
    }
}
