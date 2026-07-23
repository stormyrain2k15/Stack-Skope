using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using StackScope.Core.Analysis;
using StackScope.Core.Transactions;
using StackScope.Desktop.State;

namespace StackScope.Desktop.Behaviors;

/// <summary>
/// Attached property that turns any WPF <see cref="Selector"/>
/// (ListView, ListBox, DataGrid, ComboBox) into a real driver of
/// <see cref="SelectionState.Current"/>. Point it at the class of
/// item in the collection and this behavior copies the appropriate
/// fields onto the global selection singleton whenever the user
/// clicks a row — no per-view code-behind needed.
///
/// Usage in XAML:
///     <ListView Behaviors:SelectionDrives.IsEnabled="True" ... />
///
/// The behavior handles these row types out of the box:
///   * <see cref="TransactionEvent"/>                — full event
///   * <see cref="AttributionGraph.Node"/>           — layer/head/kind
///   * <see cref="NumericalHealth.LayerHealth"/>     — layer
///   * <see cref="QuantizationDiff.LayerShift"/>     — layer
///   * <see cref="DeterminismAuditor.Finding"/>      — layer + kind
///   * anything with a public int `Layer` / `LayerIndex` property
///     — falls back to that.
/// </summary>
public static class SelectionDrives
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(SelectionDrives),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject o, bool v) => o.SetValue(IsEnabledProperty, v);
    public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Selector sel) return;
        if ((bool)e.NewValue) sel.SelectionChanged += OnSelectionChanged;
        else sel.SelectionChanged -= OnSelectionChanged;
    }

    private static void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        var item = e.AddedItems[0];
        var s = SelectionState.Current;
        switch (item)
        {
            case TransactionEvent te:
                s.SelectEvent(te); break;
            case AttributionGraph.Node n:
                s.EventId = n.EventId; s.LayerIndex = n.Layer; s.HeadIndex = n.Head; s.Kind = n.Kind;
                break;
            case NumericalHealth.LayerHealth h:
                s.LayerIndex = h.Layer; break;
            case QuantizationDiff.LayerShift q:
                s.LayerIndex = q.Layer; break;
            case DeterminismAuditor.Finding f:
                s.LayerIndex = f.LayerIndex; s.Kind = f.Kind;
                break;
            default:
                // Duck-typed fallback: any row with a public int
                // `Layer` or `LayerIndex` property drives layer focus.
                var t = item?.GetType();
                var p = t?.GetProperty("Layer") ?? t?.GetProperty("LayerIndex");
                if (p is not null && p.PropertyType == typeof(int))
                {
                    s.LayerIndex = (int)p.GetValue(item)!;
                }
                break;
        }
    }
}
