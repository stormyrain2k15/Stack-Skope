using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StackScope.Core.Models;
using StackScope.Services;

namespace StackScope.Desktop.ViewModels;

/// <summary>
/// UI over the <see cref="AblationPresetStore"/>. Users save the current
/// AnalysisView / RunInferenceDialog ablation configuration under a
/// friendly name and load it back later with one click — the preset
/// seeds both the AblationVm (for capture) and the SweepVm (for
/// heatmaps).
/// </summary>
public sealed partial class AblationPresetsViewModel : ObservableObject
{
    private readonly ProjectService _project;
    private readonly AblationViewModel _ablationVm;
    private readonly AblationSweepViewModel _sweepVm;
    private AblationPresetStore? _store;

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private AblationPreset? _selected;
    [ObservableProperty] private string _status = "";

    public ObservableCollection<AblationPreset> Presets { get; } = new();

    public AblationPresetsViewModel(ProjectService project,
                                    AblationViewModel ablationVm,
                                    AblationSweepViewModel sweepVm)
    {
        _project    = project;
        _ablationVm = ablationVm;
        _sweepVm    = sweepVm;
        Refresh();
    }

    private AblationPresetStore GetStore()
        => _store ??= new AblationPresetStore(_project.AblationPresetsDbPath);

    [RelayCommand]
    public void Refresh()
    {
        Presets.Clear();
        foreach (var p in GetStore().List()) Presets.Add(p);
        Status = Presets.Count == 0
            ? "No presets yet — type a name, set ablation values, then Save."
            : $"{Presets.Count} preset(s).";
    }

    /// <summary>
    /// Snapshot the current AblationVm + SweepVm state into a preset
    /// under the entered name. Rejects blank names; upserts on
    /// duplicate names so users can iterate quickly.
    /// </summary>
    [RelayCommand]
    public void SaveCurrent()
    {
        if (string.IsNullOrWhiteSpace(NewName))
        {
            Status = "Enter a name for the preset first.";
            return;
        }
        var preset = new AblationPreset(
            Id: 0,
            Name: NewName.Trim(),
            LayerStart: _ablationVm.AblateLayer,
            LayerEnd:   _ablationVm.AblateLayerEnd < 0
                            ? _ablationVm.AblateLayer : _ablationVm.AblateLayerEnd,
            HeadStart:  _ablationVm.AblateHead,
            HeadEnd:    _ablationVm.AblateHeadEnd < 0
                            ? _ablationVm.AblateHead : _ablationVm.AblateHeadEnd,
            Prompt:     _sweepVm.ExtraPromptsText ?? "",
            Seed:       0,
            SigmaThreshold: _ablationVm.AutoCompareSigma,
            CreatedAtNs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L);
        var id = GetStore().Upsert(preset);
        NewName = "";
        Refresh();
        Selected = Presets.FirstOrDefault(p => p.Id == id);
        Status = $"Saved preset #{id}: {preset.Name}.";
    }

    /// <summary>
    /// Push the selected preset into both AblationVm (so the next F5
    /// capture uses it) and SweepVm (so Run sweep uses the same range),
    /// giving one-click reproduction of a saved study.
    /// </summary>
    [RelayCommand]
    public void LoadSelected()
    {
        if (Selected is null) { Status = "Pick a preset first."; return; }
        _ablationVm.AblateLayer    = Selected.LayerStart;
        _ablationVm.AblateLayerEnd = Selected.LayerEnd;
        _ablationVm.AblateHead     = Selected.HeadStart;
        _ablationVm.AblateHeadEnd  = Selected.HeadEnd;
        _ablationVm.AutoCompareSigma = Selected.SigmaThreshold;

        _sweepVm.LayerStart = Selected.LayerStart;
        _sweepVm.LayerEnd   = Selected.LayerEnd;
        _sweepVm.HeadStart  = Selected.HeadStart;
        _sweepVm.HeadEnd    = Selected.HeadEnd;
        _sweepVm.SigmaThreshold  = Selected.SigmaThreshold;
        _sweepVm.ExtraPromptsText = Selected.Prompt ?? "";

        Status = $"Loaded preset \"{Selected.Name}\" into Analysis + Sweep controls.";
    }

    [RelayCommand]
    public void DeleteSelected()
    {
        if (Selected is null) { Status = "Pick a preset first."; return; }
        var id = Selected.Id;
        int removed = GetStore().Delete(id);
        Refresh();
        Status = removed > 0 ? $"Deleted preset #{id}." : "Preset not found.";
    }
}
