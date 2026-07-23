using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace StackScope.Desktop.Converters;

/// <summary>
/// Reads the current item's index within its ListView to render a
/// 1-based rank column without polluting the data model.
/// </summary>
public sealed class ListRankConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ListViewItem item) return "";
        if (ItemsControl.ItemsControlFromItemContainer(item) is not ItemsControl parent) return "";
        return (parent.ItemContainerGenerator.IndexFromContainer(item) + 1).ToString(culture);
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
