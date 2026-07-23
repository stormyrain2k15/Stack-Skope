using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StackScope.Core.Models;
using StackScope.Services;

namespace StackScope.Desktop.ViewModels;

/// <summary>
/// Diff Pin Board — a persistent sidebar of saved (baseline ⇆ candidate)
/// diffs. Users can pin whatever <see cref="CompareDiffViewModel"/>
/// currently has loaded (left/right/sigma), attach a free-form note,
/// and re-open the pin later to seed CompareVm and re-run the diff.
///
/// The store is a project-scoped SQLite file (<c>pinned_diffs.sqlite</c>)
/// so pins survive across app sessions and are portable with the project
/// folder.
/// </summary>
public sealed partial class PinnedDiffsViewModel : ObservableObject
{
    private readonly ProjectService _project;
    private readonly CompareDiffViewModel _compareVm;
    private PinnedDiffStore? _store;

    [ObservableProperty] private string _newNote = "";
    [ObservableProperty] private string _newTags = "";
    [ObservableProperty] private PinnedDiff? _selected;
    [ObservableProperty] private string _status = "";

    public ObservableCollection<PinnedDiff> Pins { get; } = new();

    public PinnedDiffsViewModel(ProjectService project, CompareDiffViewModel compareVm)
    {
        _project = project;
        _compareVm = compareVm;
        Refresh();
    }

    private PinnedDiffStore GetStore()
        => _store ??= new PinnedDiffStore(_project.PinnedDiffsDbPath);

    /// <summary>Reload all pins from disk. Newest first.</summary>
    [RelayCommand]
    public void Refresh()
    {
        Pins.Clear();
        foreach (var p in GetStore().List()) Pins.Add(p);
        Status = Pins.Count == 0
            ? "No pins yet — hit \"Pin current\" while a diff is loaded."
            : $"{Pins.Count} pin(s).";
    }

    /// <summary>
    /// Pin whatever the Compare view currently has loaded. Refuses to
    /// pin an empty pair — no hollow rows in the sidebar.
    /// </summary>
    [RelayCommand]
    public void PinCurrent()
    {
        var left  = _compareVm.LeftTransactionId;
        var right = _compareVm.RightTransactionId;
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            Status = "Nothing to pin — load a left and right transaction first.";
            return;
        }
        var pin = new PinnedDiff(
            Id: 0,
            LeftTransactionId:  left!,
            RightTransactionId: right!,
            SigmaThreshold: _compareVm.SigmaThreshold,
            CreatedAtNs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L,
            Note: NewNote ?? "",
            Tags: NewTags ?? "");
        var id = GetStore().Add(pin);
        NewNote = ""; NewTags = "";
        Refresh();
        Selected = Pins.FirstOrDefault(p => p.Id == id);
        Status = $"Pinned #{id}: {left} ⇆ {right} @ {pin.SigmaThreshold:F2}σ.";
    }

    /// <summary>
    /// Load the currently-selected pin back into CompareVm and kick off
    /// the diff. This is the "one-click restore" that makes pinning
    /// worth the disk row.
    /// </summary>
    [RelayCommand]
    public void OpenSelected()
    {
        if (Selected is null)
        {
            Status = "Pick a pin first.";
            return;
        }
        _compareVm.LeftTransactionId  = Selected.LeftTransactionId;
        _compareVm.RightTransactionId = Selected.RightTransactionId;
        _compareVm.SigmaThreshold     = Selected.SigmaThreshold;
        _compareVm.RunCommand.Execute(null);
        Status = $"Opened pin #{Selected.Id}. Diff is running.";
    }

    /// <summary>Delete the selected pin (no confirm — Ctrl+Z isn't a
    /// thing here; the sidebar is cheap to rebuild by re-pinning).</summary>
    [RelayCommand]
    public void DeleteSelected()
    {
        if (Selected is null)
        {
            Status = "Pick a pin first.";
            return;
        }
        var id = Selected.Id;
        int removed = GetStore().Delete(id);
        Refresh();
        Status = removed > 0 ? $"Deleted pin #{id}." : $"Pin #{id} not found.";
    }

    /// <summary>
    /// Persist edits to the selected pin's note/tags. Called by the view
    /// when the user clicks "Save note".
    /// </summary>
    [RelayCommand]
    public void SaveSelectedNote()
    {
        if (Selected is null) { Status = "Pick a pin first."; return; }
        int rows = GetStore().UpdateNote(Selected.Id, NewNote ?? "", NewTags ?? "");
        Refresh();
        Status = rows > 0 ? $"Note saved on pin #{Selected.Id}." : "Nothing to save.";
    }
}
