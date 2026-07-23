using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StackScope.Desktop.ViewModels;

namespace StackScope.Desktop.Views;

public partial class KvCacheView : UserControl
{
    public KvCacheView() => InitializeComponent();

    private void OnLayerBarClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.Tag is not KvCacheViewModel.LayerRow row) return;
        if (DataContext is not KvCacheViewModel vm) return;
        vm.PickLayer(row);
        e.Handled = true;
    }
}
