using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using StackScope.Desktop.State;

namespace StackScope.Desktop.Controls;

/// <summary>
/// Renders the current cross-view selection as a clickable chip trail
/// (Transaction › Token N › Layer M › Head K). Clicking a chip clears
/// everything to its right — the standard breadcrumb navigation model.
/// </summary>
public partial class BreadcrumbBar : UserControl
{
    public sealed record Chip(string Label, string AutomationName,
                              string Level, bool ShowSeparator);

    public ObservableCollection<Chip> ChipItems { get; } = new();

    public BreadcrumbBar()
    {
        InitializeComponent();
        Chips.ItemsSource = ChipItems;
        SelectionState.Current.PropertyChanged += (_, __) => Refresh();
        Refresh();
    }

    private void Refresh()
    {
        ChipItems.Clear();
        var s = SelectionState.Current;

        var trail = new List<Chip>();
        if (!string.IsNullOrEmpty(s.TransactionId))
            trail.Add(new Chip($"Txn {Shorten(s.TransactionId!)}",
                               "Transaction " + s.TransactionId, "txn", false));
        if (s.TokenIndex >= 0)
            trail.Add(new Chip($"Token {s.TokenIndex}",
                               "Token " + s.TokenIndex, "token", false));
        if (s.LayerIndex >= 0)
            trail.Add(new Chip($"Layer {s.LayerIndex}",
                               "Layer " + s.LayerIndex, "layer", false));
        if (s.HeadIndex >= 0)
            trail.Add(new Chip($"Head {s.HeadIndex}",
                               "Head " + s.HeadIndex, "head", false));

        for (int i = 0; i < trail.Count; i++)
            ChipItems.Add(trail[i] with { ShowSeparator = i < trail.Count - 1 });
    }

    private static string Shorten(string s) =>
        s.Length > 10 ? s[..6] + "…" + s[^4..] : s;

    private void OnChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not Chip chip) return;
        var s = SelectionState.Current;
        switch (chip.Level)
        {
            case "txn":   s.TokenIndex = -1; s.LayerIndex = -1; s.HeadIndex = -1; break;
            case "token": s.LayerIndex = -1; s.HeadIndex = -1; break;
            case "layer": s.HeadIndex  = -1; break;
        }
    }
}
