using System.Windows.Controls;
using StackScope.Core.Transactions;
using StackScope.Desktop.State;

namespace StackScope.Desktop.Views;

public partial class EventListView : UserControl
{
    public EventListView()
    {
        InitializeComponent();
        EventGrid.SelectionChanged += (_, __) =>
        {
            if (EventGrid.SelectedItem is TransactionEvent e)
                SelectionState.Current.SelectEvent(e);
        };
    }
}
