using System.Windows.Controls;
using StackScope.Core.Analysis;
using StackScope.Core.Transactions;
using StackScope.Desktop.State;

namespace StackScope.Desktop.Views;

public partial class AttributionGraphView : UserControl
{
    public AttributionGraphView() => InitializeComponent();

    /// <summary>Clicking a node in the graph selects it globally so
    /// the Inspector, Timeline, and Attention Heatmap all follow.</summary>
    private void OnNodeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is not AttributionGraph.Node n) return;
        SelectionState.Current.EventId    = n.EventId;
        SelectionState.Current.LayerIndex = n.Layer;
        SelectionState.Current.HeadIndex  = n.Head;
        SelectionState.Current.Kind       = n.Kind;
    }
}
