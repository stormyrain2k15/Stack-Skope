using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StackScope.Core.Transactions;
using StackScope.Desktop.State;

namespace StackScope.Desktop.Views;

public partial class TimelineView : UserControl
{
    public TimelineView() => InitializeComponent();

    /// <summary>Click any timeline bar → select that event globally.
    /// Every other pane (Inspector, Attention heatmap, Attribution
    /// Graph, Overview) observes SelectionState and follows.</summary>
    private void OnEventBarClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.Tag is not TransactionEvent te) return;
        SelectionState.Current.SelectEvent(te);
        e.Handled = true;
    }
}
