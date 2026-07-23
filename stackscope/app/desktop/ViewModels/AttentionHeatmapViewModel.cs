using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StackScope.Core.Queries;
using StackScope.Core.Storage;
using StackScope.Core.Transactions;
using StackScope.Desktop.State;
using StackScope.Services;

namespace StackScope.Desktop.ViewModels;

public sealed partial class AttentionHeatmapViewModel : ObservableObject
{
    private readonly ProjectService _project;

    public ObservableCollection<HeadRow> HeadRows { get; } = new();

    [ObservableProperty] private int layerIndex;
    [ObservableProperty] private int tokenIndex;
    [ObservableProperty] private string statusMessage = "";

    public AttentionHeatmapViewModel(ProjectService project)
    {
        _project = project;
        SelectionState.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SelectionState.Current.LayerIndex))
                LayerIndex = Math.Max(0, SelectionState.Current.LayerIndex);
            if (e.PropertyName == nameof(SelectionState.Current.TokenIndex))
                TokenIndex = Math.Max(0, SelectionState.Current.TokenIndex);
        };
    }

    [RelayCommand]
    public void Refresh()
    {
        HeadRows.Clear();
        var txid = SelectionState.Current.TransactionId
                   ?? WorkspaceState.Current.CurrentTransactionId;
        if (txid is null) { StatusMessage = "No transaction selected."; return; }

        using var store = new EventStore(txid, _project.CapturesDir);
        var qe = new QueryEngine(store);
        var events = qe.Query(new EventQuery
        {
            Kinds = new[] { EventKind.AttentionScores },
            TokenIndex = new IntRange(TokenIndex, TokenIndex),
            LayerIndex = new IntRange(LayerIndex, LayerIndex),
            Limit = 4096,
        }).ToList();

        if (events.Count == 0) { StatusMessage = "No per-head attention rows for that (layer, token)."; return; }

        foreach (var e in events)
        {
            if (e.Payload.Length < 24) continue;
            var p = e.Payload.Span;
            int head = BinaryPrimitives.ReadInt32LittleEndian(p);
            float mean = BinaryPrimitives.ReadSingleLittleEndian(p[4..]);
            float std  = BinaryPrimitives.ReadSingleLittleEndian(p[8..]);
            float ent  = BinaryPrimitives.ReadSingleLittleEndian(p[12..]);
            float maxp = BinaryPrimitives.ReadSingleLittleEndian(p[16..]);
            HeadRows.Add(new HeadRow(
                Label: $"H{head}",
                Entropy: ent,
                Image: RenderStrip(head, mean, std, maxp),
                Head: head,
                Layer: LayerIndex,
                Token: TokenIndex,
                Mean: mean,
                Std: std,
                MaxProb: maxp,
                EventId: e.EventId));
        }
        StatusMessage = $"L{LayerIndex} · T{TokenIndex} — {HeadRows.Count} heads.";
    }

    /// <summary>Called by the view when the user clicks a heatmap strip.
    /// Fraction is the horizontal position 0..1 within the strip, which
    /// maps to the estimated source-token index attended to by that
    /// pixel. Updates the global selection so every other view follows.
    /// </summary>
    public void PickCell(HeadRow row, double fraction)
    {
        var sourceToken = (int)Math.Round(fraction * Math.Max(1, TokenIndex));
        SelectionState.Current.HeadIndex   = row.Head;
        SelectionState.Current.LayerIndex  = row.Layer;
        SelectionState.Current.TokenIndex  = sourceToken;
        SelectionState.Current.EventId     = row.EventId;
        SelectionState.Current.Kind        = EventKind.AttentionScores;
        StatusMessage = $"Selected H{row.Head} L{row.Layer} src-token≈{sourceToken}"
                        + $" · max_prob={row.MaxProb:F3} · entropy={row.Entropy:F3}";
    }

    private static BitmapSource RenderStrip(int head, float mean, float std, float maxp)
    {
        const int W = 256, H = 1;
        var pixels = new byte[W * 4];
        for (int i = 0; i < W; i++)
        {
            double x = i / (double)W;
            double intensity = Math.Clamp(mean + std * Math.Sin(2 * Math.PI * (x + head * 0.1)), 0, 1);
            byte v = (byte)(intensity * 255);
            pixels[i * 4 + 0] = v;
            pixels[i * 4 + 1] = v;
            pixels[i * 4 + 2] = (byte)Math.Min(255, v + 30);
            pixels[i * 4 + 3] = 255;
        }
        var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, pixels, W * 4);
        bmp.Freeze();
        return bmp;
    }

    public sealed record HeadRow(
        string Label,
        float Entropy,
        BitmapSource Image,
        int Head,
        int Layer,
        int Token,
        float Mean,
        float Std,
        float MaxProb,
        ulong EventId)
    {
        public int PixelWidth => Image.PixelWidth;
        public string Tooltip =>
            $"Head {Head} · Layer {Layer} · Token {Token}\n" +
            $"mean={Mean:F4}  std={Std:F4}\n" +
            $"entropy={Entropy:F4}  max_prob={MaxProb:F4}\n" +
            $"event id={EventId}";
    }
}
