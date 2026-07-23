using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StackScope.Desktop.Commands;

namespace StackScope.Desktop.Views;

public partial class CommandPaletteWindow : Window
{
    public CommandPaletteWindow()
    {
        InitializeComponent();
        Refresh("");
        Loaded += (_, __) => Search.Focus();
    }

    private void Refresh(string query)
    {
        Results.Items.Clear();
        foreach (var cmd in StackScope.Desktop.Commands.Commands.All)
        {
            if (string.IsNullOrEmpty(query) ||
                cmd.Text.Contains(query, System.StringComparison.OrdinalIgnoreCase))
            {
                var item = new ListBoxItem
                {
                    Content = cmd.Text,
                    Tag = cmd,
                    Foreground = System.Windows.Media.Brushes.White,
                };
                Results.Items.Add(item);
            }
        }
        if (Results.Items.Count > 0) Results.SelectedIndex = 0;
    }

    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => Refresh(Search.Text);

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down) { Results.Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter) Activate();
        else if (e.Key == Key.Escape) Close();
    }

    private void OnResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Activate();
        else if (e.Key == Key.Escape) Close();
    }

    private void OnActivate(object sender, System.Windows.Input.MouseButtonEventArgs e) => Activate();

    private void Activate()
    {
        if (Results.SelectedItem is ListBoxItem { Tag: RoutedUICommand cmd })
        {
            Close();
            if (Owner is not null && cmd.CanExecute(null, Owner))
                cmd.Execute(null, Owner);
        }
    }
}
