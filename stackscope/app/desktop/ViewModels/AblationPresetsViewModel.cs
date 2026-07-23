using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
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

    /// <summary>
    /// Serialise the selected preset to a portable
    /// <c>.stackscope-preset.json</c> file so studies can be shared
    /// across teams. Uses <see cref="SaveFileDialog"/>; refuses when
    /// nothing is picked. The exported schema is stable — Import()
    /// on any StackScope project reads it back verbatim.
    /// </summary>
    [RelayCommand]
    public void ExportSelected()
    {
        if (Selected is null) { Status = "Pick a preset first."; return; }
        var dlg = new SaveFileDialog
        {
            Title    = "Export ablation preset",
            Filter   = "StackScope preset (*.stackscope-preset.json)|*.stackscope-preset.json|All files (*.*)|*.*",
            FileName = SafeFileName(Selected.Name) + ".stackscope-preset.json",
        };
        if (dlg.ShowDialog() != true) { Status = "Export cancelled."; return; }
        var payload = new ExportedAblationPreset
        {
            SchemaVersion = 1,
            Name = Selected.Name,
            LayerStart = Selected.LayerStart,
            LayerEnd   = Selected.LayerEnd,
            HeadStart  = Selected.HeadStart,
            HeadEnd    = Selected.HeadEnd,
            Prompt     = Selected.Prompt,
            Seed       = Selected.Seed,
            SigmaThreshold = Selected.SigmaThreshold,
        };
        try
        {
            File.WriteAllText(dlg.FileName,
                JsonSerializer.Serialize(payload,
                    new JsonSerializerOptions { WriteIndented = true }));
            Status = $"Exported \"{Selected.Name}\" → {dlg.FileName}";
        }
        catch (Exception ex) { Status = "Export failed: " + ex.Message; }
    }

    /// <summary>
    /// Import a shared preset from a <c>.stackscope-preset.json</c>
    /// file. Upserts by name so importing over an existing preset
    /// updates it rather than creating a duplicate. Rejects
    /// schema-version mismatches with a clear error.
    /// </summary>
    [RelayCommand]
    public void ImportFromFile()
    {
        var dlg = new OpenFileDialog
        {
            Title  = "Import ablation preset",
            Filter = "StackScope preset (*.stackscope-preset.json)|*.stackscope-preset.json|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) { Status = "Import cancelled."; return; }
        try
        {
            var raw = File.ReadAllText(dlg.FileName);
            var pkg = JsonSerializer.Deserialize<ExportedAblationPreset>(raw)
                       ?? throw new InvalidOperationException("empty file");
            if (pkg.SchemaVersion != 1)
                throw new InvalidOperationException(
                    $"unsupported schema version {pkg.SchemaVersion} (expected 1)");
            if (string.IsNullOrWhiteSpace(pkg.Name))
                throw new InvalidOperationException("preset name is missing");
            var id = GetStore().Upsert(new AblationPreset(
                Id: 0, Name: pkg.Name,
                LayerStart: pkg.LayerStart, LayerEnd: pkg.LayerEnd,
                HeadStart:  pkg.HeadStart,  HeadEnd:  pkg.HeadEnd,
                Prompt: pkg.Prompt ?? "", Seed: pkg.Seed,
                SigmaThreshold: pkg.SigmaThreshold,
                CreatedAtNs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L));
            Refresh();
            Selected = Presets.FirstOrDefault(p => p.Id == id);
            Status = $"Imported \"{pkg.Name}\" (#{id}).";
        }
        catch (Exception ex) { Status = "Import failed: " + ex.Message; }
    }

    private static string SafeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "preset" : s;
    }
}
