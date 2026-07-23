using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StackScope.Core.Queries;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;
using StackScope.Desktop.State;

namespace StackScope.Desktop.Controls;

public partial class TokenScrubber : UserControl
{
    public static readonly DependencyProperty CurrentTokenProperty =
        DependencyProperty.Register(nameof(CurrentToken), typeof(int), typeof(TokenScrubber),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnCurrentTokenChanged));
    public static readonly DependencyProperty MaxTokenProperty =
        DependencyProperty.Register(nameof(MaxToken), typeof(int), typeof(TokenScrubber),
            new PropertyMetadata(0));

    public int CurrentToken { get => (int)GetValue(CurrentTokenProperty); set => SetValue(CurrentTokenProperty, value); }
    public int MaxToken     { get => (int)GetValue(MaxTokenProperty);     set => SetValue(MaxTokenProperty, value); }

    public TokenScrubber()
    {
        InitializeComponent();
        Name = "Self";
        Loaded += (_, __) => RecomputeBounds();
        SelectionState.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SelectionState.Current.TransactionId))
                RecomputeBounds();
            if (e.PropertyName == nameof(SelectionState.Current.TokenIndex))
                CurrentToken = Math.Max(0, SelectionState.Current.TokenIndex);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Left)  { CurrentToken = Math.Max(0, CurrentToken - 1); e.Handled = true; }
            if (e.Key == Key.Right) { CurrentToken = Math.Min(MaxToken, CurrentToken + 1); e.Handled = true; }
        };
    }

    private static void OnCurrentTokenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is int v && SelectionState.Current.TokenIndex != v)
        {
            var s = SelectionState.Current;
            if (s.TransactionId is not null) s.SelectToken(s.TransactionId, v);
        }
    }

    private void OnStepBack(object sender, RoutedEventArgs e)
        => CurrentToken = Math.Max(0, CurrentToken - 1);
    private void OnStepForward(object sender, RoutedEventArgs e)
        => CurrentToken = Math.Min(MaxToken, CurrentToken + 1);

    private void RecomputeBounds()
    {
        var txid = SelectionState.Current.TransactionId
                   ?? WorkspaceState.Current.CurrentTransactionId;
        if (txid is null) { MaxToken = 0; return; }
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StackScope", "default", "captures");
        var sqlite = Path.Combine(dir, txid + ".sqlite");
        if (!File.Exists(sqlite)) { MaxToken = 0; return; }
        try
        {
            using var store = new EventStore(txid, dir);
            using var cmd = store.Index.Connection.CreateCommand();
            cmd.CommandText = "SELECT MAX(token_index) FROM events WHERE token_index >= 0;";
            var r = cmd.ExecuteScalar();
            MaxToken = r is long l ? (int)l : 0;
        }
        catch { MaxToken = 0; }
    }
}
