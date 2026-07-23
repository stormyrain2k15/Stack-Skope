using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StackScope.Core.Models;
using StackScope.Services;

namespace StackScope.Desktop.ViewModels;

/// <summary>
/// One pin as displayed in the WPF sidebar — the underlying
/// <see cref="PinnedDiff"/> plus a live health check that resolves
/// each transaction id against the project. A pin whose left or
/// right capture was deleted is flagged <see cref="IsDangling"/>
/// so the sidebar renders it in red instead of silently failing to
/// run when the user clicks "Open pin".
/// </summary>
public sealed record PinnedDiffRow(
    PinnedDiff Pin,
    bool       LeftMissing,
    bool       RightMissing)
{
    public long   Id                 => Pin.Id;
    public string LeftTransactionId  => Pin.LeftTransactionId;
    public string RightTransactionId => Pin.RightTransactionId;
    public double SigmaThreshold     => Pin.SigmaThreshold;
    public string Note               => Pin.Note;
    public string Tags               => Pin.Tags;
    public bool   IsDangling         => LeftMissing || RightMissing;
    public string StatusIcon         => IsDangling ? "⚠" : "✓";
    public string StatusTooltip => (LeftMissing, RightMissing) switch
    {
        (true,  true)  => "Both captures were deleted from the project.",
        (true,  false) => "Left (baseline) capture was deleted from the project.",
        (false, true)  => "Right (candidate) capture was deleted from the project.",
        _              => "Both captures resolve — pin is healthy.",
    };
}

/// <summary>
/// Diff Pin Board — a persistent sidebar of saved (baseline ⇆ candidate)
/// diffs. Users can pin whatever <see cref="CompareDiffViewModel"/>
/// currently has loaded (left/right/sigma), attach a free-form note,
/// and re-open the pin later to seed CompareVm and re-run the diff.
///
/// The store is a project-scoped SQLite file (<c>pinned_diffs.sqlite</c>)
/// so pins survive across app sessions and are portable with the project
/// folder. Each row is health-checked against the live transaction list
/// so dangling pins are visible instead of silent.
/// </summary>
public sealed partial class PinnedDiffsViewModel : ObservableObject
{
    private readonly ProjectService _project;
    private readonly CompareDiffViewModel _compareVm;
    private PinnedDiffStore? _store;

    [ObservableProperty] private string _newNote = "";
    [ObservableProperty] private string _newTags = "";
    [ObservableProperty] private PinnedDiffRow? _selected;
    [ObservableProperty] private string _status = "";

    public ObservableCollection<PinnedDiffRow> Pins { get; } = new();

    public PinnedDiffsViewModel(ProjectService project, CompareDiffViewModel compareVm)
    {
        _project = project;
        _compareVm = compareVm;
        Refresh();
    }

    private PinnedDiffStore GetStore()
        => _store ??= new PinnedDiffStore(_project.PinnedDiffsDbPath);

    /// <summary>Reload all pins from disk, health-checked against
    /// the current transaction list. Newest first.</summary>
    [RelayCommand]
    public void Refresh()
    {
        Pins.Clear();
        var known = _project.ListTransactions()
            .Select(t => t.TransactionId).ToHashSet(StringComparer.Ordinal);
        int dangling = 0;
        foreach (var p in GetStore().List())
        {
            bool leftMissing  = !known.Contains(p.LeftTransactionId);
            bool rightMissing = !known.Contains(p.RightTransactionId);
            if (leftMissing || rightMissing) dangling++;
            Pins.Add(new PinnedDiffRow(p, leftMissing, rightMissing));
        }
        Status = Pins.Count == 0
            ? "No pins yet — hit \"Pin current\" while a diff is loaded."
            : dangling == 0
                ? $"{Pins.Count} pin(s), all resolved."
                : $"{Pins.Count} pin(s), {dangling} dangling (missing capture).";
    }
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
    /// the diff. Refuses to run when the pin is dangling — the user
    /// gets a clear status message instead of an empty diff table.
    /// </summary>
    [RelayCommand]
    public void OpenSelected()
    {
        if (Selected is null)
        {
            Status = "Pick a pin first.";
            return;
        }
        if (Selected.IsDangling)
        {
            Status = $"Pin #{Selected.Id} is dangling — {Selected.StatusTooltip} "
                     + "Delete the pin or recapture the missing transaction.";
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
