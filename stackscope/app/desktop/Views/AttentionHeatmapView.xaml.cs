using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StackScope.Desktop.ViewModels;

namespace StackScope.Desktop.Views;

public partial class AttentionHeatmapView : UserControl
{
    public AttentionHeatmapView() => InitializeComponent();

    /// <summary>
    /// Convert a click on a heatmap strip to a source-token pick. The
    /// pixel-fraction along the image maps to a source token index;
    /// the ViewModel updates SelectionState so every other view
    /// follows.
    /// </summary>
    private void OnHeadStripClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Image img) return;
        if (img.Tag is not AttentionHeatmapViewModel.HeadRow row) return;
        if (DataContext is not AttentionHeatmapViewModel vm) return;

        var pos = e.GetPosition(img);
        double frac = Math.Clamp(pos.X / Math.Max(1.0, img.ActualWidth), 0.0, 1.0);
        vm.PickCell(row, frac);
        e.Handled = true;
    }
}
