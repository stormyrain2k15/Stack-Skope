using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using StackScope.Core.Transactions;

namespace StackScope.Desktop.Converters;

/// <summary>Nanoseconds → milliseconds, formatted to 3dp.</summary>
public sealed class NsToMillisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long ns) return (ns / 1_000_000.0).ToString("F3", culture);
        return "";
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Event kind → accent brush (uses palette from Dark.xaml).</summary>
public sealed class KindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not EventKind k) return Brushes.Gray;
        return k switch
        {
            EventKind.TokenBegin or EventKind.TokenEnd
                => (Brush)System.Windows.Application.Current.Resources["Brush.Accent.Violet"],
            EventKind.LayerBegin or EventKind.LayerEnd
                => (Brush)System.Windows.Application.Current.Resources["Brush.Accent.Amber"],
            EventKind.AttentionQkv or EventKind.AttentionScores or EventKind.AttentionOutput
                => (Brush)System.Windows.Application.Current.Resources["Brush.Accent.Cyan"],
            EventKind.Activation
                => (Brush)System.Windows.Application.Current.Resources["Brush.Accent.Sage"],
            EventKind.KernelLaunch or EventKind.KernelEnd
                => (Brush)System.Windows.Application.Current.Resources["Brush.Accent.Rust"],
            EventKind.Alloc or EventKind.Free or EventKind.Memcpy
                => (Brush)System.Windows.Application.Current.Resources["Brush.Accent.Slate"],
            EventKind.Logits or EventKind.Sample
                => (Brush)System.Windows.Application.Current.Resources["Brush.Accent.Violet"],
            _ => (Brush)System.Windows.Application.Current.Resources["Brush.Text.Muted"]
        };
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Boolean → Collapsed/Visible.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is bool b && b) ? System.Windows.Visibility.Visible
                                  : System.Windows.Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

/// <summary>Boolean → inverse boolean. Used to enable/disable buttons.</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => v is bool b ? !b : Binding.DoNothing;
}
