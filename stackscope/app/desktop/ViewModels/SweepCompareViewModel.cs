using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StackScope.Services;

namespace StackScope.Desktop.ViewModels;

/// <summary>
/// One triangulated cell in the Sweep Compare view — layer/head shared,
/// plus σ from the left sweep, σ from the right sweep, and the delta.
/// Deep red = big absolute delta; useful for spotting heads that
/// matter for one model / prompt-set but not the other.
/// </summary>
public sealed partial class SweepCompareCell : ObservableObject
{
    [ObservableProperty] private int _layer;
    [ObservableProperty] private int _head;
    [ObservableProperty] private double _leftSigma;
    [ObservableProperty] private double _rightSigma;

    public double Delta => RightSigma - LeftSigma;

    public string Tooltip =>
        $"L{Layer} · H{Head}\nleft σ = {LeftSigma:F3}\nright σ = {RightSigma:F3}\nΔ = {Delta:+0.000;-0.000;0.000}";

    public Brush Fill
    {
        get
        {
            double a = Math.Clamp(Math.Abs(Delta) / 5.0, 0.0, 1.0);
            byte lo = 0x22;
            byte hi = 0xE0;
            byte channel = (byte)(lo + a * (hi - lo));
            // Positive delta (right > left) → red; negative → blue. Users can
            // read "which sweep the head matters more in" straight from the tile.
            return Delta >= 0
                ? new SolidColorBrush(Color.FromRgb(channel, 0x33, 0x33))
                : new SolidColorBrush(Color.FromRgb(0x33, 0x33, channel));
        }
    }

    partial void OnLeftSigmaChanged(double v)  { OnPropertyChanged(nameof(Delta)); OnPropertyChanged(nameof(Fill)); OnPropertyChanged(nameof(Tooltip)); }
    partial void OnRightSigmaChanged(double v) { OnPropertyChanged(nameof(Delta)); OnPropertyChanged(nameof(Fill)); OnPropertyChanged(nameof(Tooltip)); }
}

/// <summary>
/// Loads two sweep result files (the resume JSON written by
/// <see cref="AblationSweepViewModel"/>) and overlays them into a
/// single delta-heatmap. Useful when comparing "does the same head
/// matter in model A vs model B" or "…in prompt-set A vs B".
/// </summary>
public sealed partial class SweepCompareViewModel : ObservableObject
{
    private readonly ProjectService _project;

    [ObservableProperty] private string? _leftFile;
    [ObservableProperty] private string? _rightFile;
    [ObservableProperty] private string _status = "Pick two sweep result files.";
    public ObservableCollection<SweepCompareCell> Cells { get; } = new();
    public ObservableCollection<string> AvailableSweeps { get; } = new();

    public int LayerCount { get; private set; } = 1;
    public int HeadCount  { get; private set; } = 1;

    public SweepCompareViewModel(ProjectService project)
    {
        _project = project;
        RefreshAvailable();
    }

    /// <summary>Scan the project root for <c>sweep-resume-*.json</c>
    /// files so the picker doesn't force the user to type paths.</summary>
    [RelayCommand]
    public void RefreshAvailable()
    {
        AvailableSweeps.Clear();
        try
        {
            foreach (var f in Directory.EnumerateFiles(_project.RootDir, "sweep-resume-*.json")
                                       .OrderByDescending(File.GetLastWriteTimeUtc))
                AvailableSweeps.Add(Path.GetFileName(f));
        }
        catch { /* project may not exist yet */ }
        Status = AvailableSweeps.Count == 0
            ? "No sweep result files found in this project."
            : $"{AvailableSweeps.Count} sweep file(s) available.";
    }

    /// <summary>
    /// Load both files, intersect their (Layer, Head) cells, and build
    /// the compare grid. First-column (prompt index 0) is used from
    /// each file — cross-prompt overlays are out of scope for this
    /// pass; users who need per-prompt overlays run the Sweep Compare
    /// once per prompt.
    /// </summary>
    [RelayCommand]
    public void LoadAndCompare()
    {
        if (string.IsNullOrWhiteSpace(LeftFile) || string.IsNullOrWhiteSpace(RightFile))
        {
            Status = "Pick both left and right sweep files.";
            return;
        }
        var leftPath  = Path.Combine(_project.RootDir, LeftFile!);
        var rightPath = Path.Combine(_project.RootDir, RightFile!);
        if (!File.Exists(leftPath) || !File.Exists(rightPath))
        {
            Status = "One or both files are missing.";
            return;
        }
        Dictionary<(int L, int H), double> Load(string path)
        {
            var raw = File.ReadAllText(path);
            var rows = JsonSerializer.Deserialize<List<ResumeRow>>(raw) ?? new();
            // Use the first prompt column only — that's the reliable
            // apples-to-apples baseline column both sweeps always emit.
            var firstPrompt = rows
                .GroupBy(r => r.Prompt ?? "")
                .OrderBy(g => g.Key)
                .FirstOrDefault();
            var m = new Dictionary<(int, int), double>();
            if (firstPrompt is null) return m;
            foreach (var r in firstPrompt) m[(r.Layer, r.Head)] = r.Sigma;
            return m;
        }
        Dictionary<(int L, int H), double> left, right;
        try { left  = Load(leftPath);  right = Load(rightPath); }
        catch (Exception ex) { Status = "Load failed: " + ex.Message; return; }

        var keys = left.Keys.Concat(right.Keys).Distinct()
            .OrderBy(k => k.L).ThenBy(k => k.H).ToList();
        if (keys.Count == 0) { Status = "No cells in either file."; return; }

        int lMin = keys.Min(k => k.L), lMax = keys.Max(k => k.L);
        int hMin = keys.Min(k => k.H), hMax = keys.Max(k => k.H);
        LayerCount = Math.Max(1, lMax - lMin + 1);
        HeadCount  = Math.Max(1, hMax - hMin + 1);
        OnPropertyChanged(nameof(LayerCount));
        OnPropertyChanged(nameof(HeadCount));

        Cells.Clear();
        for (int l = lMin; l <= lMax; l++)
        {
            for (int h = hMin; h <= hMax; h++)
            {
                Cells.Add(new SweepCompareCell
                {
                    Layer = l, Head = h,
                    LeftSigma  = left.TryGetValue((l, h), out var ls)  ? ls  : 0.0,
                    RightSigma = right.TryGetValue((l, h), out var rs) ? rs : 0.0,
                });
            }
        }
        Status = $"{Cells.Count} cells. Left {Path.GetFileNameWithoutExtension(LeftFile)}, "
                 + $"Right {Path.GetFileNameWithoutExtension(RightFile)}. "
                 + $"Peak |Δ| = {Cells.Max(c => Math.Abs(c.Delta)):F3}.";
    }

    private sealed class ResumeRow
    {
        public string? Prompt { get; set; }
        public int    Layer  { get; set; }
        public int    Head   { get; set; }
        public string? TxId  { get; set; }
        public double Sigma  { get; set; }
    }
}
